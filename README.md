# CraftQueue

CraftQueue moves normal SPT hideout crafting into a central, local web interface. Crafts can be queued across stations, monitored, retried and cancelled without opening each hideout area.

Version 1.1.0 targets SPT 4.0.13. Both parts must be installed: the BepInEx client plugin and the server mod.

## Features

- Queue any number of crafts per station; each station works through its queue in order, across all stations in parallel.
- Finished crafts are collected automatically and the next queued craft starts on its own - also **while you are in a raid**, where the server takes over the queue.
- Live overview of every running craft with product names, icons, remaining time and a paused indicator when the generator is off. The view keeps working while EFT sits in a menu, loads, or is closed entirely (estimated from the saved profile).
- Full recipe browser with search by product, requirement, category or station - showing exactly the recipes your profile has unlocked.
- Cancelling a running craft returns its **tools** to the stash (vanilla would silently destroy them). If the stash has no room for them, the cancel is refused instead. Ingredients stay consumed, as in vanilla.
- Queue entries survive server restarts. Interrupted starts become a visible `Needs Review` entry instead of a silent duplicate or loss.
- Press **F9** in game to open your personal web link in the default browser.

CraftQueue also repairs a vanilla SPT 4.0.13 defect that makes every hideout craft cancellation crash server-side (a request-deserialization bug); the fix disables itself automatically once SPT resolves it.

## Limitations

- Continuous productions (water collector, bitcoin farm), the scav case and the cultist circle are not queueable and are left untouched.
- Outside raids the queue advances only while EFT is running, because all inventory changes go through the real client (see the safety model below). Crafts that finish while EFT is closed are collected at the next start.
- Active crafts cannot be aborted during a raid.

## Safety model

CraftQueue uses two execution modes:

- In the main menu and the 3D hideout, the BepInEx client performs starts, collection and cancellation through EFT's normal hideout calls. SPT's regular item-event response therefore keeps the live client inventory synchronized.
- During an actual raid, EFT deliberately unloads the stash from its local profile. CraftQueue hands the queue to the server for that window, uses SPT's normal `HideoutController` logic to start and collect crafts, and stops the handover before SPT merges the post-raid inventory. EFT then reloads the authoritative profile, including all consumed components and produced items.

This split avoids changing the server stash while an out-of-raid client still has an interactable cached copy. Active crafts cannot be aborted during a raid.

Before an entry is accepted, the connected EFT client checks:

- whether the recipe is unlocked and available for the selected profile;
- the exact local item suitability rules, including FIR, functional, encoded and pinned items;
- consumables already needed by queued crafts;
- tools across parallel areas, while allowing tool reuse by sequential crafts in the same area.

Resources are not physically removed when an entry is queued. If the stash changes afterwards, CraftQueue keeps the entry and retries later instead of bypassing the normal game transaction.

The client-side recipe list is retained for the duration of a raid. New entries can therefore be added from the web interface while raiding, and the server validates them against the authoritative stash plus the already queued requirements. EFT must have reported that profile's available recipes at least once before the raid.

Server-side starts use a persisted `Starting` state before mutating the profile. A successfully started production is saved before its queue entry is removed, so an interrupted server write becomes a visible review case instead of an automatic duplicate start.

An ambiguous start is never issued twice automatically. It is shown as `Needs Review`; verify the hideout in game, then retry or remove that queue entry.

## Installation

Use the release archive and copy its `BepInEx` and `SPT_Runtime` folders into the SPT installation.

Start the SPT server and EFT. The server log prints a one-time URL such as:

`http://127.0.0.1:38473/?token=...`

Open that complete URL. Choose the desired profile if the installation contains more than one.

## Configuration

The server configuration is stored at:

`SPT_Runtime/user/mods/CraftQueue/config/config.json`

The default allows LAN access, so the mod works out of the box when EFT and the SPT server run on different machines:

```json
{
  "allowLan": true,
  "bindAddress": "0.0.0.0",
  "webPort": 38473
}
```

Set `allowLan` to `false` to restrict the web interface to the server machine itself.

`webPort` changes the port of the web interface - useful when another
application already owns 38473. Only the server needs to know: EFT asks it for
the port, so the in-game link follows automatically. A config file without
`webPort` keeps the default. If the port cannot be opened, the server logs the
reason and the mod keeps working through the SPT backend (the queue runs, only
the web interface is unreachable).

Access is protected by two kinds of tokens:

- The **host link** printed in the server console grants full access to every profile - keep it private.
- Each EFT client receives a **personal link** in its BepInEx log (`your personal web interface: ...`). That link only ever sees and controls its own profile, so on a co-op server every player can be given their own link safely.

Press **F9** in game to open your personal interface, or click the **CRAFT QUEUE** button in the bottom menu bar next to HIDEOUT (removable via `Show menu bar button`). By default the interface appears as a window over the game; **left Shift+F9** opens it in the external browser instead (its own configurable shortcut). Close the overlay with **F9** or **Escape** while it is focused, with its close button, or by clicking back into the game and pressing F9 again. The window can be moved and resized like any other, and its title bar can be turned off entirely (`Show window title bar`). All keys and the overlay behaviour live in the BepInEx configuration (`Web Interface`). Tokens are minted per server start; the F9 key and the logs always carry the current link.

The overlay is provided by the **[Anvil-WebOverlay](https://github.com/maschine34675/WebOverlay)** library, an optional dependency: install it alongside CraftQueue to get the in-game window. Without the library - or without the Microsoft WebView2 runtime it needs (current Windows 10 and 11 installations already include it), or in exclusive fullscreen, where a window over the game would minimise it - CraftQueue silently uses the external browser instead. Run the game in borderless windowed mode for the overlay.

## Mod compatibility

- **Hideout In Progress (hip)**: compatible - hip handles area upgrades, CraftQueue handles productions; the two share no patched method, route or data.
- **Hideout Automation**: not compatible with its production stacking - it is a second craft-queue system whose server patch can silently divert craft starts. At minimum disable `ProductionStacking` in its config; its server half stays active regardless, so running only one of the two mods is the safe choice.

## Co-op (Fika)

Fika compatibility is verified in a live co-op session: every player gets their own profile-scoped link, queues are strictly per profile, and the plugin is fully inert on a headless host. A raid whose end is never reported (crashed client, headless) is released from server handover automatically after a generous timeout.

## Changelog

Release history in [CHANGELOG.md](CHANGELOG.md).

## Building

The projects no longer terminate EFT or deploy to a hard-coded installation during an ordinary build.

```powershell
dotnet build CraftQueue.slnx -c Release
dotnet test CraftQueue.Tests\CraftQueue.Tests.csproj -c Release
```

An explicit local deployment is available by passing both properties:

```powershell
dotnet build CraftQueue.slnx -c Debug -p:DeployToSpt=true -p:SptRoot=C:\Path\To\SPT
```

Create a clean release archive with:

```powershell
.\scripts\New-ReleasePackage.ps1
```

## License

MIT. See [LICENSE](LICENSE).

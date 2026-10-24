# Changelog

## 1.1.0

Unreleased update for Idle Slayer 7.3.6 (Steam build 26139124), covering compatibility fixes, craftable strategies and refinery planning.

### Fixed

- Fixed refinery collection checks by comparing inventory and queue changes instead of broken Interop tuple getters. Uncertain results stop retries and include quantity diagnostics.
- Added idle refinery refill toward material capacity in one-output batches, balancing stored and queued stock. It protects raw reserves and active-burst Rage Pills, continues through Souls windows, and does not trigger travel or Jewel spending. Capacity-blocked queues remain occupied.
- Limited Unity registration to four parameterless `StrategyHost` callbacks. Hook failures stay isolated; task failures clear stale plans and pause only the affected module.
- Connected critical-hit task supply to raw-material collection and refining, crediting natural quest progress during waits. Stock targets use observed consumption, replenishment time and native effects, including critical caps and active bonuses. Shared effects do not reserve duplicate recipes; unknown-yield items such as Bunny Paw use surplus inputs without dedicated farming or paid acceleration.
- Unified refinery execution and forecasts in one bounded scheduler covering existing queues, input availability and batch sizes. Recipes for both Spyglasses, Critical Sword and the three new supported items respect the Village building/Book of Refinement unlock, stock, reserves and capacity. Input routes preserve specific-target quests; completed output remains collectible after events end, without automatic cancellation or discarding.
- Added separate English/Chinese settings for Bunny Paw (event searches), Death Bringer (ordinary Giant combat) and CpS Spyglass (coin-multiplier windows). Normal mode waits for effects to expire; Bunny Paw also waits during Coins From Random Box quests. AFK keeps all three unlocked effects active without material use, regardless of their normal-mode switches.
- Unified Paid Souls and refinery spending under a persistent UTC daily budget, defaulting to 50 Jewels, while retaining the refinery sub-budget. Migration preserves recorded refinery spending and pending charges. Purchases are marked pending before invocation; uncertain charges block both consumers across restarts until reconciled.
- Restored automatic Paid Souls purchases through the native callback, with live price, balance, reserve and effect checks plus exact charge/result confirmation. Refinery slot purchases and acceleration have separate default-off switches and complete-recipe benefit checks. Neither adapter requires a game-version or binary-hash match; manually purchased bonuses remain usable.
- Fixed Sleepy Hollow mount floating by releasing the mod's jump/upward velocity and yielding jump, Boost, Wind Dash and bow control throughout Rage preparation, execution and ending. Normal control resumes afterward.
- Updated quests to the data-object API with stable IDs/calibration names and native eligibility, replacing the removed UI cache. A shared policy excludes ineffective event-credit items from estimates, crafting, supply and reservations, while preserving useful real-action effects and native collab exceptions. Future activities reuse known-type rules across game updates; unrecognized tasks are skipped in isolation. Event samples stay outside ordinary calibration. Location tasks retain their destination and re-entry handling; rerolls remain Daily/Weekly-only.
- Switched Chest Hunt preparation to the stable `StartEventDelayed` entry point, removing reliance on a version-dependent generated coroutine class.
- Restored specific-Giant routing, including Giant Gorilla, when bow automation and rapid Giant attacks are enabled. Critical-hit quests remain background objectives and do not displace specific-enemy routes. Automation stays suspended through save-upload exit, including after UI removal.

### Compatibility

- Targets Idle Slayer 7.3.6; the old 7.0.6 quest enum API is unsupported.
- Unknown task types are skipped for planning and mutation but still occupy Daily/Weekly slots, preventing replacement sets from overwriting them.
- Removed game-version gates and hash checks from runtime and packaging. Journal writes use direct content comparison; older journals retain spending and pending charges. Native interfaces must still exist, and live-state, budget and result checks remain in place.

### Verification

- Release compilation and static native-reference checks passed against fresh 7.3.6 interfaces. Task, refinery and purchase code was inspected without executing a purchase.
- Full in-game validation remains pending, including UI, task progress, event multipliers, hooks and refinery behavior.

## 1.0.0

Initial public release.

### Added

- Unified quest selection, material routing, temporary-item planning, travel, combat and event handling under one strategy controller.
- Joint map planning for concurrent objectives, strategic unlock priority and adaptive Daily and Weekly reroll decisions.
- Smart batch restocking for Whetstone, quest boosters and rare-material reserves, with idle surplus farming and best-Souls-map travel.
- Automatic jumping, regular Boost, rapid Giant attacks, Rage planning, Wind Dash quest support and grounded Random Box approaches.
- Chest Hunt preparation that coordinates eligible Souls bonuses and short-lived craftables around the entry window.
- Smart Strategy-owned Daily and Weekly regeneration, unlimited reroll eligibility and optional zero-cooldown Portal control.
- An in-game settings panel with draft, Apply and Cancel semantics, setting dependencies and live language refresh.
- English and Simplified Chinese UI localization with English fallback and native game-language fonts.
- Hotkeys for master control, emergency stop, subsystem toggles and detailed status snapshots.
- Structured English logs with stable categories, concise release output and opt-in debug diagnostics.
- Optional endgame client effects behind a separate master switch, default-off state and explicit risk warnings.

### Safety and compatibility

- Detects enabled AutoProgression quest mutation controls without writing its configuration, and blocks only new AutoProgression Ascensions during an active paid Souls burst.
- Stops strategy mutations during the native save-upload exit flow and prevents duplicate controllers across scene transitions.
- Records game, game-assembly, Common-library, runtime and configuration-schema versions at startup.
- Centralizes version-sensitive Unity paths, external-mod contracts and craftable-effect values for compatibility reviews.

### Packaging

- Builds against a user-supplied Idle Slayer installation without storing game or third-party assemblies in the repository.
- Ships a release archive containing the mod DLL, README, changelog, license and README banner.
- Publishes consistent author, repository and MIT license metadata across the assembly, source archive, GitHub and Nexus Mods.
- Includes structured GitHub bug-report, feature-request and pull-request templates plus automatic release-note categories.

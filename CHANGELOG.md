# Changelog

## 1.1.0

Unreleased update for Idle Slayer 7.3.6 (Steam build 26139124), covering compatibility fixes, craftable strategies and refinery planning.

### Fixed

- Added normal-mode strategies and separate English/Chinese settings for Bunny Paw, Death Bringer and CpS Spyglass. They support event searches, ordinary Giant combat and coin-multiplier windows, respectively. The mod waits for an active effect to expire before crafting another. Bunny Paw also waits while a tracked Coins From Random Box quest needs coin rewards.
- Added refinery supply for both Spyglasses, Critical Sword and the three additional craftables. The planner combines recipe demand, counts stock and queued output, and prepares replacement materials before buffs expire. It checks the Village building and Book of Refinement unlock, then limits each batch by demand, reserves and capacity.
- Added refinery input routes that protect critical crafting stock and preserve routes for quests with specific targets. The mod can collect completed outputs after the original event ends. It keeps queues intact when output does not fit, without cancelling jobs or discarding materials.
- Added separate switches for Jewel slot purchases and acceleration, both off by default. Estimates account for existing queues, available raw materials and the time needed to finish a complete recipe. Payments require the inspected game hash, a matching live price, sufficient reserves and a persisted UTC daily budget. The mod checks the exact deduction and result; an unresolved charge blocks further refinery spending across restarts.
- AFK/endgame permanent craftables keep all three effects active together after their crafting prerequisites are unlocked. Their normal-mode switches do not limit this behavior, and maintaining these effects consumes no materials.
- Automatic jump, Boost, Wind Dash and bow control now yield during Rage preparation, execution and ending. The mod releases its own unfinished jump and upward velocity before native Rage takes over, preventing the Sleepy Hollow mount from drifting upward in its kinematic movement state. Normal automation resumes after Rage ends.
- Updated task handling for the game's new data-object API. Types are mapped through `QuestTypes.list` to stable internal keys, preserving existing calibration names and numeric values. Ordinary quests are read from loaded game objects with native eligibility checks, without relying on the removed task-list UI cache.
- Active map-event quests are planned and claimed only while the event and task set are available. Those conditions are checked again before execution. Rerolls apply only to Daily and Weekly slots.
- Visit Location and Kill Enemies in Location keep their required destination when quests are planned together. If a visit still needs an arrival while the player is already there, the route can leave and return without changing the quest's stored destination.
- Event estimates, crafting and material plans use the game's raw progress rules. They no longer count quest staffs or other inapplicable progress multipliers. Raw-count samples are kept out of ordinary quest calibration.
- Chest Hunt preparation uses the stable `StartEventDelayed` entry point, avoiding a generated coroutine class whose number changed in the game update.
- Native hook groups load independently, so one incompatible hook does not prevent the others from loading. A task scan or planning failure clears stale plans and pauses that module before retrying; other strategy modules can continue.
- Restored automatic Paid Souls purchases for the inspected Steam build. The adapter checks the exact GameAssembly SHA-256 before resolving the native purchase callback. Price, balance, reserve and existing effects are checked again immediately before the call.
- Purchase calls are marked pending before invocation, including calls that throw after charging. Further automatic purchases in the same game session remain blocked until the exact charge and matching active effect are confirmed. Manually purchased bonuses can still be used by the strategy.
- Specific Giant quests remain reachable when bow automation is assumed and rapid Giant attacks are enabled. Targets that cannot be killed with arrows, such as the Giant Gorilla, can again direct travel to their home dimension.
- Automation stays suspended throughout the game's save-upload exit flow, including after the UI has been removed.
- Critical-hit quests remain background objectives. They no longer affect joint map selection or displace reachable specific-enemy routes such as Carniplants in Jungle.

### Compatibility

- This build targets the new 7.3.6 task API and does not support the old 7.0.6 quest enum API.
- Unknown task types are skipped for planning, crafting, claiming and rerolling. An unknown active Daily or Weekly task still occupies its slot, preventing the mod from generating a replacement set over it.
- Automatic purchases are disabled on game binaries whose purchase flow has not been checked. Manually activated paid bonuses can still be used for Rage chaining.

### Verification

- Release compilation against freshly generated 7.3.6 interfaces and static checks of native member references passed. The relevant task-counting, refinery and purchase code was inspected without executing a purchase.
- In-game validation is still pending, including UI paths, live task progress, event multipliers, native hook installation and refinery behavior.

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
- Ships a release archive containing the mod DLL, README, changelog, license, SHA-256 checksum and README banner.
- Publishes consistent author, repository and MIT license metadata across the assembly, source archive, GitHub and Nexus Mods.
- Includes structured GitHub bug-report, feature-request and pull-request templates plus automatic release-note categories.
- Reports separate SHA-256 hashes for the release DLL and final ZIP archive.

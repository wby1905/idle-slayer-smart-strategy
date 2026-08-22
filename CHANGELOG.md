# Changelog

## 1.0.2

### Fixed

- Application shutdown now latches at the MelonLoader lifecycle boundary. Strategy ticks stay suspended after Unity begins its save-upload exit flow, even after the game UI has already been torn down.

## 1.0.1

### Fixed

- Critical-hit quests now remain background-only opportunities. They no longer influence joint map selection or suppress reachable specific-enemy routes such as Carniplants in Jungle.

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

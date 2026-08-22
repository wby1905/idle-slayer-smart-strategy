# Changelog

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

- Detects AutoProgression quest-controller conflicts without changing another mod's configuration.
- Stops strategy mutations during the native save-upload exit flow and prevents duplicate controllers across scene transitions.
- Records game, game-assembly, Common-library, runtime and configuration-schema versions at startup.
- Centralizes version-sensitive Unity paths, external-mod contracts and craftable-effect values for compatibility reviews.

### Packaging

- Builds against a user-supplied Idle Slayer installation without storing game or third-party assemblies in the repository.
- Ships a release archive containing the mod DLL, README, changelog, license, SHA-256 checksum and README banner.

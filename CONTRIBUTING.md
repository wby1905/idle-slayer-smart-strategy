# Contributing

## Local setup

Build Smart Strategy against a local Idle Slayer installation. Set `IDLE_SLAYER_GAME_DIR`, pass `IdleSlayerGameDir` to MSBuild, or copy `SmartStrategy.local.props.example` to the ignored `SmartStrategy.local.props` file.

Do not commit game files, MelonLoader binaries, other mods, generated build output, logs, saves or local configuration files.

Use the issue forms to report bugs with reproduction steps or request a specific feature. For vulnerabilities involving saves, accounts, purchases or other sensitive data, follow `SECURITY.md` and open a private security advisory.

## Code boundaries

- Put external Unity names and hierarchy paths in `GameContracts.cs`.
- Put optional AutoProgression names and configuration keys in `AutoProgressionContract.cs`.
- Put version-sensitive craftable values in `CraftableEffectModel.cs`.
- Put fixed native-state coordination delays in `RuntimeCadence.cs`; expose actual strategy choices through `SmartConfig`.
- Route runtime logs through `SmartLog` and write them in English.
- Add user-facing text through `SmartSettingsLocalization` with English fallback.
- Keep source, comments, configuration descriptions, logs, diagnostics, and documentation in English. Non-English user-facing text belongs only in the centralized localization catalog.
- Keep settings dependencies typed with `SmartSettingId`.
- Keep native task-type mapping and event/location rules in `QuestApi.cs`.
- Keep Paid Souls compatibility checks and pending-charge tracking in `PaidSoulsPurchaseCompatibility.cs`.
- Keep refinery queue operations in `RefinedCraftingSupply.cs` and completion-time estimates in `RefineryForecast.cs`. Count existing queues as committed supply; do not assume their outputs can be replaced by duplicate production in an empty slot.
- Keep refinery payment checks and the persistent budget journal in `RefineryPremiumPolicy.cs`. Preserve the pending-charge guard when changing purchase behavior.

Keep each file focused. Use early returns when they clarify the flow, and contain optional-integration failures so unrelated quest, material, travel and combat systems can continue.

## Verification

Build the Release configuration against interfaces from the current game version, then check the diff for accidentally included binaries or machine-specific paths. A build against interfaces left over from an earlier game version does not establish compatibility with an update.

Use a backed-up save for in-game checks and include the startup compatibility snapshot in bug reports. Distinguish behavior observed in the game from behavior checked through compilation or static inspection. For refinery changes, keep capacity limits, manual queues and uncertain charges covered by that review; compilation alone does not verify those runtime paths.

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
- Keep quest-item applicability in `QuestEffectPolicy.cs` and use it at both planning and execution boundaries. Do not infer quest semantics from event names or matching type-ID strings; the game compares native quest-type objects. Task supply must retain its item purpose so changed rules can invalidate it.
- Keep native Paid Souls and refinery checks in their adapters. Check interfaces and live state without game-version or binary-hash allowlists. Route automatic Jewel consumption through `JewelPurchaseCoordinator.cs` and its durable `JewelBudgetJournal.cs`; a subsystem must not keep a separate retry or budget path.
- Keep recipe expansion in `CraftSupplyPlanner.cs` and native marginal-effect formulas in `CraftableYieldModel.cs`. Separate observed opportunity rates from game-provided effect values, and leave unknown yields explicit.
- Keep refinery queue operations in `RefinedCraftingSupply.cs`. Execution and `RefineryForecast.cs` must use the same `RefinerySchedule.cs` plan. Count existing queues as committed supply; do not replace them with duplicate production in an empty slot.

Keep each file focused. Use early returns when they clarify the flow, and contain optional-integration failures so unrelated quest, material, travel and combat systems can continue.

## Verification

Build the Release configuration against interfaces from the current game version, then check the diff for accidentally included binaries or machine-specific paths. A build against interfaces left over from an earlier game version does not establish compatibility with an update.

Use a backed-up save for in-game checks and include the startup compatibility snapshot in bug reports. Distinguish behavior observed in the game from behavior checked through compilation or static inspection. For refinery changes, keep capacity limits, manual queues and uncertain charges covered by that review; compilation alone does not verify those runtime paths.

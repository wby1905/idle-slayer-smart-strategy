# Contributing

## Local setup

Smart Strategy compiles against assemblies from a local Idle Slayer installation. Set `IDLE_SLAYER_GAME_DIR`, pass `IdleSlayerGameDir` to MSBuild, or copy `SmartStrategy.local.props.example` to the ignored `SmartStrategy.local.props` file.

Do not commit game files, MelonLoader binaries, other mods, generated build output, logs, saves or local configuration files.

Use the repository's issue forms for reproducible bugs and focused feature requests. Follow `SECURITY.md` and open a private security advisory for save, account, purchase or other security-sensitive problems.

## Code boundaries

- Put external Unity names and hierarchy paths in `GameContracts.cs`.
- Put optional AutoProgression names and configuration keys in `AutoProgressionContract.cs`.
- Put version-sensitive craftable values in `CraftableEffectModel.cs`.
- Put fixed native-state coordination delays in `RuntimeCadence.cs`; expose actual strategy choices through `SmartConfig`.
- Route runtime logs through `SmartLog`; log messages are canonical English.
- Add user-facing text through `SmartSettingsLocalization` with English fallback.
- Keep source, comments, configuration descriptions, logs, diagnostics, and documentation in English. Non-English user-facing text belongs only in the centralized localization catalog.
- Keep settings dependencies typed with `SmartSettingId`.

Prefer small focused files, early returns and explicit failure isolation. A missing optional integration must not stop quest, material, travel or combat systems that do not depend on it.

## Verification

At minimum, build the Release configuration against a valid local game installation and inspect the diff for accidentally included binaries or machine-specific paths. Runtime verification should use a backed-up save and include the startup compatibility snapshot when reporting a problem.

# Smart Strategy for Idle Slayer

![Smart Strategy pixel-art planning banner](docs/images/smart-strategy-hero.png)

**Smart Quests · Smart Materials · Smart Combat**

Smart Strategy keeps your Slayer working on the best available objective. It evaluates quests together, chooses a dimension, plans material runs, crafts temporary items when the expected time saved exceeds the restock time, and coordinates combat, Rage and event windows.

Standard automation and endgame effects have separate switches. Endgame mode starts disabled.

## Download and source

- Download the release build from [Nexus Mods](https://www.nexusmods.com/idleslayer/mods/49).
- Browse the source, report bugs and request features on [GitHub](https://github.com/wby1905/idle-slayer-smart-strategy).

The Nexus archive is the supported installation package. GitHub source checkouts are intended for development and do not include game assemblies or third-party dependencies.

## Core features

- **Quest planning:** claims and generates quest sets, learns Daily completion times, rerolls poor objectives and prioritizes quests that unlock useful content.
- **Joint routing:** scores active quests and missing materials together, then chooses one dimension for the current plan.
- **Material strategy:** restocks Whetstone and quest boosters in useful batches, protects rare-material reserves and fills common materials during idle periods.
- **Combat and events:** controls jumping, regular Boost, rapid Giant attacks and Rage decisions while reserving boxes, keys and Portal approaches.
- **Chest Hunt preparation:** stages eligible Souls bonuses and short-lived craftables around the entry window.
- **Optional endgame effects:** maintains selected client-side timers and multipliers behind a separate master switch and an in-game warning.

## Requirements

- Idle Slayer on Windows
- [MelonLoader 0.7.2 or later](https://www.nexusmods.com/idleslayer/mods/6) / .NET 6 runtime used by the game
- [Idle Slayer Mods Core 1.3.2 or later](https://www.nexusmods.com/idleslayer/mods/3), which provides `IdleSlayerMods.Common.dll`

The current compatibility baseline is Idle Slayer 7.0.6, MelonLoader 0.7.2 and IdleSlayerMods.Common 1.3.2. Other versions may work, but they have not been verified against this release.

The mod is version-sensitive because it calls game IL2CPP types directly. After an Idle Slayer update, review the MelonLoader log before leaving the game unattended.

## Installation

1. Install MelonLoader and Idle Slayer Mods Core from the links above.
2. Copy `SmartStrategy.dll` into the game's `Mods` directory.
3. Start the game and open **Smart Strategy** from the information-panel button list.

Only the DLL is required. Do not install source files or build output folders. Release archives also include the README, changelog, license and a SHA-256 checksum file.

## First run and controls

Open **Smart Strategy** from the information panel, review the switches, then press **Apply**. The panel follows the game language. Simplified Chinese and English are included; unsupported languages use English.

The panel exposes curated boolean switches in five sections:

- Basic controls
- Quest strategy
- Combat and events
- Materials and travel
- Endgame and permanent bonuses

Switches edit a temporary draft. Nothing changes or saves until **Apply** is pressed. Canceling or closing the popup discards the draft. Numeric thresholds, timing values and hotkeys remain in `UserData/MelonPreferences.cfg` for advanced users.

Use these keys during play:

- `F9`: master automation toggle or recover from an emergency stop
- `F10`: emergency stop
- `F4`: movement and smart-combat toggle
- `F5`: quest automation toggle
- `F6`: material-travel toggle
- `F7`: write a detailed status snapshot to the log

## AutoProgression compatibility

Smart Strategy does not require AutoProgression. You can run both mods, but only one mod should control each overlapping system.

| Area | Behavior when both mods are installed |
| --- | --- |
| Quest claiming, generation, filtering and Daily or Weekly rerolls | Smart Strategy owns these actions. Set `[Quests] Enabled = false` in `UserData/AutoProgression.cfg`. |
| Portal cooldown reset | Smart Strategy owns this only when its zero-cooldown Portal switch is enabled. Disabling AutoProgression's Quests section prevents both mods from resetting the same Portal state. |
| Automatic Ascension | AutoProgression runs normally. During an active Smart Strategy Paid Souls burst, Smart Strategy blocks the start of a new Ascension until the Souls bonus ends. If AutoProgression already started an Ascension, its trailing Buy All phase can finish. |
| All other AutoProgression sections | They remain independent. Smart Strategy does not enable, disable or rewrite them. |

Smart Strategy reads `AutoProgression.cfg` only to detect an enabled quest controller. It shows one compatibility warning and leaves the file unchanged. If the optional Ascension guard cannot find the expected AutoProgression API, Smart Strategy logs the compatibility failure and leaves AutoProgression behavior unchanged.

## Logging

Runtime logs use stable English messages and category prefixes such as `[Quest]`, `[Material]`, `[Portal]`, `[Combat]` and `[Compatibility]`. This keeps reports searchable across game languages while the in-game UI and notifications remain localized.

Release logging is limited to meaningful state changes, user actions, warnings and errors. Enable `Debug Logging` in `MelonPreferences.cfg` when diagnosing planning, calibration, UI mounting or compatibility behavior. `F7` always produces an explicit detailed status snapshot. Startup also records the game, game-assembly, Common-library, runtime and configuration-schema versions.

When reporting a problem, include the Smart Strategy version, Idle Slayer version, relevant configuration values and the smallest useful section of `MelonLoader/Latest.log`.

## Safety and progression impact

Back up your save before the first run and before enabling high-impact options. Smart Strategy can call native game actions, consume inventory and accelerate progression.

- Standard automation is enabled on a new configuration. Open the in-game panel before leaving a new installation unattended; `F10` immediately stops Smart Strategy actions.
- Quest rerolls and the optional zero-cooldown Portal setting bypass ordinary waiting limits.
- Quest crafting, Rage Pills, temporary boosters and the Dragon Egg option can consume real inventory.
- `Paid Souls Chest Burst` can spend Jewels of Soul after its configured progression, reserve and Chest Hunt conditions are satisfied. It does not repeat a charge after detecting a reduced balance without a live effect.
- Endgame mode is disabled by default. It directly maintains client-side timed effects, can activate paid-style 500x Souls and CpS effects without a purchase call, and removes several short-window routing guards. This mode changes the normal progression model.

Disabling endgame mode stops Smart Strategy from maintaining those effects; active effects then return to their native lifecycle. Removing the DLL does not remove the preferences file, so review `UserData/MelonPreferences.cfg` before reinstalling a later version.

## Endgame options

Endgame mode can keep eligible client-side timed effects and the configured 500x bonuses active. These options change progression and use a separate master switch. Review them before using the mod with a save you care about.

The retired free-egg loop and Bonus Stage forced-exit watchdog are not part of the release build.

## Building

The project targets .NET 6 and resolves game assemblies from a local Idle Slayer installation. Game files and third-party binaries are never stored in this repository.

Configure the installation with an environment variable:

```powershell
$env:IDLE_SLAYER_GAME_DIR = "X:\Games\Steam\steamapps\common\Idle Slayer"
dotnet build .\SmartStrategy\SmartStrategy.csproj -c Release
```

Alternatively, copy `SmartStrategy.local.props.example` to the ignored `SmartStrategy.local.props` file and edit the path, or pass the property directly:

```powershell
dotnet build .\SmartStrategy\SmartStrategy.csproj -c Release -p:IdleSlayerGameDir="X:\path\to\Idle Slayer"
```

Create a distributable archive with:

```powershell
.\build-release.ps1
```

The build fails early when the game directory or `IdleSlayerMods.Common.dll` is missing. The release DLL is written to `SmartStrategy/bin/Release/net6.0/SmartStrategy.dll`; build output and release artifacts are ignored by Git. The release command prints separate SHA-256 hashes for the DLL and final ZIP archive.

## Architecture and compatibility

- `GameContracts.cs` owns Unity scene, hierarchy and world-object names used by the mod.
- `AutoProgressionContract.cs` isolates the optional AutoProgression integration surface.
- `CraftableEffectModel.cs` is the single source for version-sensitive temporary-item effect values used by planning and execution.
- `RuntimeCadence.cs` contains fixed native-state coordination delays; player-tunable thresholds stay in `SmartConfig`.
- `SmartSettingsCatalog.cs` declares the typed in-game setting catalog and dependency graph.
- `SmartLog.cs` is the only direct boundary to MelonLoader logging.

After an Idle Slayer or dependency update, compare the startup compatibility snapshot and review these contracts before changing strategy code.

## Scope and support

Smart Strategy is an unofficial community mod and is not affiliated with the Idle Slayer developer. It does not automatically manage the enabled state of other mods.

See [CONTRIBUTING.md](CONTRIBUTING.md) for repository conventions, [SECURITY.md](SECURITY.md) for responsible vulnerability reporting and [GitHub Issues](https://github.com/wby1905/idle-slayer-smart-strategy/issues) for public bug reports and feature requests.

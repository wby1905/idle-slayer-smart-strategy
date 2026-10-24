# Smart Strategy for Idle Slayer

![Smart Strategy pixel-art planning banner](docs/images/smart-strategy-hero.png)

**Smart Quests · Smart Materials · Smart Combat**

Smart Strategy compares active quests and material needs to choose the next objective and dimension. It plans material runs and coordinates combat, Rage and events. For temporary-item supply, it weighs the expected time saved against the time needed to restock.

Standard automation and endgame effects have separate switches. Endgame mode starts disabled.

## Download and source

- Download the release build from [Nexus Mods](https://www.nexusmods.com/idleslayer/mods/49).
- Browse the source, report bugs and request features on [GitHub](https://github.com/wby1905/idle-slayer-smart-strategy).

Use the Nexus archive to install the mod. The GitHub repository contains the source for development; game assemblies and third-party dependencies are not included.

## Core features

- **Quest planning:** claims rewards, generates quest sets, learns Daily completion times, rerolls slow objectives and prioritizes quests that unlock useful content.
- **Joint routing:** scores active quests and missing materials together, then chooses one dimension for the current plan.
- **Material strategy:** restocks Whetstone and quest boosters in useful batches, protects rare-material reserves and fills common materials during idle periods.
- **Combat and events:** controls jumping, regular Boost, rapid Giant attacks and Rage, while adjusting movement to reach boxes, keys and Portals.
- During Rage preparation, execution and ending, native game movement takes control. Automatic jumping, Boost, Wind Dash and bow attacks resume after Rage ends.
- **Chest Hunt preparation:** applies eligible Souls bonuses and temporary craftables before entry.
- **Additional craftables:** in normal mode, uses Bunny Paw to search for Souls events, Death Bringer for ordinary Giant combat, and CpS Spyglass during coin-multiplier windows. It waits for each effect to expire before crafting another. Bunny Paw also waits while a tracked Coins From Random Box objective needs coin rewards.
- **Refinery planning:** prepares shared ingredients for Bunny Paw, Death Bringer, both Spyglasses and Critical Sword. It counts inventory and queued output, limits each batch to available materials and capacity, and collects completed output that fits. Optional input farming respects quest priorities. Jewel spending has separate switches, both off by default.
- **Optional endgame effects:** keeps selected client-side timers and multipliers active. These effects have a separate master switch and an in-game warning.

## Requirements

- Idle Slayer on Windows
- [MelonLoader 0.7.2 or later](https://www.nexusmods.com/idleslayer/mods/6) / .NET 6 runtime used by the game
- [Idle Slayer Mods Core 1.3.2 or later](https://www.nexusmods.com/idleslayer/mods/3), which provides `IdleSlayerMods.Common.dll`

Version 1.1.0 is an unreleased update for Idle Slayer 7.3.6 (Steam build 26139124), with compatibility fixes, additional craftable strategies and refinery planning. It compiles against freshly generated game interfaces using the installed MelonLoader toolchain and IdleSlayerMods.Common 1.3.2. Static interface checks have passed, and the relevant native code has been inspected. In-game validation is still pending. This build uses the new data-object quest API and does not support the old 7.0.6 quest enum API.

The task planner now handles active map-event quests, Visit Location and Kill Enemies in Location. It checks whether the event and task set are available and respects the required destination. Event objectives use raw progress, so the planner does not count ordinary quest-booster multipliers toward them. Unknown task types are skipped.

Quest estimates, crafting and supply share one item-applicability policy. Raw-count activities do not trigger kill-credit staffs, the collectable-credit necklace or Random Box Staff, including their material reservations and refinery requests. Items that create more real opportunities, such as Giant spawns, drops or critical hits, remain eligible when they help the target. The native collab Rage-kill exception remains supported. New activities using known native quest types and source markers inherit these rules without an event-name list or game-version check. An unrecognized task is skipped without disabling other tasks. Independent Souls/material strategies and free AFK effects keep their own controls.

No feature requires a particular game version or game-binary hash. Automatic purchases use the available native methods and check live prices, balances, budgets and results. A missing interface disables the affected operation. Bonuses purchased manually can still be used for Rage chaining.

Smart Strategy calls game IL2CPP types directly, so game updates can break compatibility. Check the MelonLoader log after an update before leaving the game unattended.

## Installation

1. Install MelonLoader and Idle Slayer Mods Core from the links above.
2. Copy `SmartStrategy.dll` into the game's `Mods` directory.
3. Start the game and open **Smart Strategy** from the information-panel button list.

Only the DLL is required. Do not install source files or build output folders. Release archives also include the README, changelog, license and README banner.

## First run and controls

Open **Smart Strategy** from the information panel, review the switches, then press **Apply**. The panel follows the game language. Simplified Chinese and English are included; unsupported languages use English.

The panel groups its on/off switches into five sections:

- Basic controls
- Quest strategy
- Combat and events
- Materials and travel
- Endgame and permanent bonuses

Changes take effect and are saved only when you press **Apply**. Canceling or closing the popup discards them. Numeric thresholds, timing values and hotkeys are configured in `UserData/MelonPreferences.cfg`.

Use these keys during play:

- `F9`: master automation toggle or recover from an emergency stop
- `F10`: emergency stop
- `F4`: movement and smart-combat toggle
- `F5`: quest automation toggle
- `F6`: material-travel toggle
- `F7`: write a detailed status snapshot to the log

## Refinery strategy

Finish the refinery building in the Village and obtain the Book of Refinement from Jack. You can then run in other dimensions while the mod manages the native queues. The mod leaves the Village popup closed and checks the unlock before starting work. The game handles online timers and offline progress.

The default supply lookahead is 45 minutes. A longer estimated supply chain extends that horizon. The planner combines shared ingredients and uses observed consumption intervals to size replacement stock, with up to three working copies plus configured reserves, subject to capacity. Recipe batches produce at most six outputs; the scheduler uses the same rules for execution and forecasts.

After funded recipe work, the mod uses remaining free slots to refill refined materials toward their native capacity. It favors lower projected stock, counting queued output, and uses raw-material fullness to break ties. Background batches default to one output so new recipe needs do not wait behind long speculative jobs. This refill uses surplus inputs without dedicated travel or Jewel spending. A slot waiting for bag space remains occupied. Turning off `Refinery Fill Idle Slots To Capacity` restores the smaller `Refinery Idle Stock Per Material` target, which defaults to two.

Dedicated supply must justify its cost. The mod reads each supported item's native effect and weighs it against observed useful opportunity time, raw-material acquisition and refinery work. It learns from runner time, excluding long pauses and offline time. During the first 60 seconds of observations, or when an item's yield is unknown, it uses surplus inputs without requesting dedicated farming. Bunny Paw stays in this surplus-only category because its effect on the Random Box selection process does not establish a direct Souls-yield ratio. It does not authorize paid acceleration for Bunny Paw.

Critical-hit quest supply uses the current capped critical probability to compare direct completion with farming, refining and crafting. Existing queues keep running during material acquisition, and the estimate credits quest progress during the remaining wait. Several quests can share one item effect without reserving duplicate recipes. These are estimates, not a guarantee of globally optimal routes or measured farming gains.

Raw-material supply protects Whetstone, Rage Pill and current quest-staff stock. Quests with a specific target keep their route. The planner can collect refinery inputs along that route or arrange a separate material trip after higher-priority work ends. It subtracts queued output before requesting more inputs, so you can continue quests while refining runs. AFK/endgame permanent effects need no recipe supply; the separate surplus buffer can still use spare raw stock.

The mod keeps manual queues intact and collects a completed portion only when it all fits in the bag. It verifies collection through the inventory increase and the queue's claimed count or cleared state, avoiding broken generic return-field getters in the current Interop build. It does not cancel jobs or discard output. Press `F7` to see slot timers, queued quantities and capacity blocks. If a manual batch has more completed output than the bag can hold, collect it through the game UI. An uncertain native inventory change suspends further refinery changes for that session and logs the observed quantities.

The **Materials and travel** section contains separate controls for automatic refining, input farming, the surplus buffer, Jewel slot purchases and Jewel acceleration. Both paid options default to **off**. Numeric settings in `UserData/MelonPreferences.cfg` include:

| Setting | Default |
| --- | --- |
| `Refinery Maximum Slots` | 3, bounded by the game's limit |
| `Refinery Idle Batch Outputs` | 1; short background batches leave slots available sooner for recipe work |
| `Refinery Jewel Reserve` | 50; the larger Paid Souls reserve also applies |
| `Refinery Daily Jewel Budget` | 50 across refinery actions per UTC day |
| `Global Jewel Reserve` | 50; the largest global, Paid Souls or refinery reserve applies to all automatic purchases |
| `Global Daily Jewel Budget` | 50 total across Paid Souls and refinery purchases per UTC day; zero disables automatic spending |
| `Refinery Minimum Slot Gain Minutes` | 30 minutes saved on the raw-funded recipe backlog |
| `Refinery Minimum Speedup Minutes` | 5 minutes saved on the complete recipe |
| `Refinery Minimum Seconds Saved Per Jewel` | 240 seconds per Jewel |

On the inspected 7.3.6 build, one output consumes 100 raw materials and takes 10 minutes. An extra slot costs 30 Jewels; acceleration costs 2 Jewels per remaining cycle, rounded up. The mod reads these native values at runtime and rechecks each paid action against its live quote.

Background queue maintenance can continue during Souls and paid-burst windows. The mod protects the Rage Pill stock needed by the active burst and any critical supply plan; it does not treat ordinary bag-filling targets as mandatory reserves. A scene transition, disabled automation or an unsafe runner state still stops new actions.

The mod can accelerate only a queue it created and has tracked without interruption. The recipe's other ingredients must be ready, and existing queues must cover its missing refined materials. For a timed event, the recipe must become ready before the event ends. Slot purchases depend on the estimated benefit of extra production capacity; a new slot does not speed up jobs already running. The mod does not spend Jewels to fill the surplus buffer.

Paid Souls and refinery purchases share `UserData/SmartStrategy_JewelBudget.json`. Before calling a native purchase method, the mod records the charge and reserved budget, flushes the file and compares the saved bytes with the written content, without computing a hash. It preserves spending and pending charges from older journals while ignoring their retired digest field. It then rechecks the operation, price, balance and current limits. It clears the pending marker only after confirming both the exact Jewel deduction and the native result. An unresolved charge blocks both consumers across restarts; free refining and AFK effects can continue.

On migration, the mod imports recorded refinery spending and unresolved charges from `UserData/SmartStrategy_RefineryBudget.cfg`. It keeps the old file intact and blocks spending if that file later changes. Previously unrecorded Paid Souls purchases cannot be reconstructed. Check the in-game balance and purchase result before repairing either journal; restarting or crossing a UTC date boundary does not clear an unresolved charge.

## AutoProgression compatibility

Smart Strategy does not require [AutoProgression 2.0](https://www.nexusmods.com/idleslayer/mods/40). You can run both mods, but only one mod should control each overlapping system.

| Area | Behavior when both mods are installed |
| --- | --- |
| Quest claiming, generation, filtering and Daily or Weekly rerolls | Smart Strategy owns these actions. Set `[Quests] Enabled = false` in `UserData/AutoProgression.cfg`. |
| Portal cooldown reset | Smart Strategy owns this only when its zero-cooldown Portal switch is enabled. Disabling AutoProgression's Quests section prevents both mods from resetting the same Portal state. |
| Automatic Ascension | AutoProgression runs normally. During an active Smart Strategy Paid Souls burst, Smart Strategy blocks the start of a new Ascension until the Souls bonus ends. If AutoProgression already started an Ascension, its trailing Buy All phase can finish. |
| All other AutoProgression sections | They remain independent. Smart Strategy does not enable, disable or rewrite them. |

Smart Strategy reads `AutoProgression.cfg` to check whether its quest controller is enabled. If it is, Smart Strategy shows one warning without changing the file. If the optional Ascension guard cannot find the expected AutoProgression API, it logs the failure and leaves AutoProgression running as usual.

## Logging

Logs use English messages with category prefixes such as `[Quest]`, `[Material]`, `[Portal]`, `[Combat]` and `[Compatibility]`, so reports can be searched across game languages. The in-game UI and notifications remain localized.

Release logs record state changes, user actions, warnings and errors. Enable `Debug Logging` in `MelonPreferences.cfg` for planning, calibration, UI setup or compatibility diagnostics. `F7` writes a detailed status snapshot regardless of that setting. Startup logs include the game, game-assembly, Common-library, runtime and configuration-schema versions.

When reporting a problem, include the exact Smart Strategy and Idle Slayer version numbers, relevant settings and a short excerpt from `MelonLoader/Latest.log`. For an exception, include the first error and its stack trace. Please use version numbers rather than "latest" so the report can be matched to a game build.

## Safety and progression impact

Back up your save before the first run and before enabling high-impact options. Smart Strategy can call native game actions, consume inventory and accelerate progression.

- Standard automation is enabled on a new configuration. Open the in-game panel before leaving a new installation unattended; `F10` immediately stops Smart Strategy actions.
- Quest rerolls and the optional zero-cooldown Portal setting bypass ordinary waiting limits.
- Quest crafting, Rage Pills, temporary boosters and the Dragon Egg option can consume real inventory.
- `Paid Souls Chest Burst` can spend Jewels of Soul after checking progression, stock, price, shared reserve and daily budget. It uses the game's purchase callback and can also use manually purchased bonuses.
- Refinery slot purchases and acceleration can spend Jewels if you enable their separate switches. Both default to off. They share the global daily budget with Paid Souls and also respect the refinery sub-budget. An unresolved charge blocks both automatic consumers across restarts, as described above. Manual purchases are outside the mod's budget.
- Endgame mode is disabled by default. It directly maintains client-side timed effects, can activate paid-style 500x Souls and CpS effects without a purchase call, and removes several short-window routing guards. This mode changes the normal progression model.

Disabling endgame mode lets active effects expire under the game's normal rules. Removing the DLL leaves the preferences file in place, so review `UserData/MelonPreferences.cfg` before reinstalling a later version.

## Endgame options

Endgame mode keeps eligible client-side timed effects and the configured 500x bonuses active through a separate master switch. Review these options before enabling them on a save you care about; they change normal progression.

With permanent timed craftables enabled, AFK/endgame mode keeps Bunny Paw, Death Bringer and CpS Spyglass active together once their crafting prerequisites are unlocked. Their normal-mode switches and situational checks do not restrict this mode. No crafting or refining materials are spent to maintain these permanent effects.

The retired free-egg loop and Bonus Stage forced-exit watchdog are not part of the release build.

## Building

The project targets .NET 6 and builds against a local Idle Slayer installation. The repository does not contain game files or third-party binaries.

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

After a game update, regenerate the MelonLoader interfaces before building. To use a separate directory of fresh interfaces, pass `-p:IdleSlayerAssembliesDir="X:\path\to\fresh\interop"` to the build, or `-AssembliesDirectory` to `build-release.ps1`. These game assemblies are build inputs only; do not include them in release archives.

The build fails early when the game directory or `IdleSlayerMods.Common.dll` is missing. The release DLL is written to `SmartStrategy/bin/Release/net6.0/SmartStrategy.dll`; build output and release artifacts are ignored by Git. The release command prints the version and DLL/ZIP paths without generating checksum files.

## Architecture and compatibility

- `GameContracts.cs` stores the Unity scene, hierarchy and world-object names the mod uses.
- `AutoProgressionContract.cs` stores the names used by the optional AutoProgression integration.
- `CraftableEffectModel.cs` defines the version-sensitive temporary-item effect values shared by planning and execution.
- `RuntimeCadence.cs` defines the fixed delays needed for native game state to settle; player-adjustable thresholds stay in `SmartConfig`.
- `SmartSettingsCatalog.cs` defines the in-game settings and their dependencies.
- `SmartLog.cs` routes all logging through MelonLoader.
- `QuestApi.cs` maps native task objects to stable internal types and checks event, location and progress rules.
- `QuestEffectPolicy.cs` governs quest-item effects across estimation, crafting, supply and reservations. It distinguishes direct progress bonuses from real actions and invalidates obsolete task supply.
- `RefineryStrategy.cs` connects craftable demand and material routes to `RefinedCraftingSupply.cs`, which manages native refinery queues.
- `CraftSupplyPlanner.cs` expands complete recipes into acquisition routes and refinery work. `CraftableYieldModel.cs` reads native marginal effects; `CraftingDemandPolicy.cs` uses opportunity and consumption observations to decide whether to restock.
- `RefinerySchedule.cs` supplies the deterministic batch plan used by execution and `RefineryForecast.cs`. Existing queued output cannot be replaced with duplicate production.
- `JewelPurchaseCoordinator.cs` owns the cross-consumer payment gate; `JewelBudgetJournal.cs` stores the durable budget and pending charge. `PaidSoulsPurchaseCompatibility.cs` and `RefineryPremiumPolicy.cs` handle native eligibility and result checks.

After an Idle Slayer or dependency update, compare the startup compatibility snapshot and review these contracts before changing strategy code.

## Scope and support

Smart Strategy is an unofficial community mod and is not affiliated with the Idle Slayer developer. It does not automatically manage the enabled state of other mods.

See [CONTRIBUTING.md](CONTRIBUTING.md) for repository conventions, [SECURITY.md](SECURITY.md) for responsible vulnerability reporting and [GitHub Issues](https://github.com/wby1905/idle-slayer-smart-strategy/issues) for public bug reports and feature requests.

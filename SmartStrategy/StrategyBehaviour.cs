using Il2Cpp;
using UnityEngine;
using UnityEngine.UI;

namespace SmartStrategy;

public sealed partial class StrategyBehaviour : MonoBehaviour
{
    internal static StrategyBehaviour Current { get; private set; }

    private readonly CraftableCatalog _catalog = new();
    private MaterialPlanner _materials;
    private QuestDirector _quests;
    private readonly Dictionary<SmartItem, float> _craftBackoffUntil = new();
    private readonly Dictionary<SmartItem, float> _nextCraftSkipLogAt = new();
    private float _nextTick;
    private float _runnerValidSince = -1f;
    private float _lastCraftAt = -100f;
    private float _sceneReadyAt;
    private float _nextMaterialScan;
    private float _nextMaterialDeepReplan;
    private bool _materialPlanDirty = true;
    private float _nextIdleSoulMapScan;
    private float _portalRequestedAt = -1000f;
    private float _lastDimensionChangeAt = -1000f;
    private string _lastMapId;
    private string _lastMaterialFocus;
    private bool _emergencyStopped;
    private bool _giantBattleLogged;
    private bool _giantAttackInterfaceLogged;
    private bool _giantCapturePromptLogged;
    private float _nextGiantAttackAt;
    private float _nextGiantAttackErrorLogAt;
    private bool _soulEventWasActive;
    private bool _hordeWasActive;
    private bool _soulBurstDeferred;
    private bool _soulDeferredLogWritten;
    private bool _soulRecoveryLogWritten;
    private bool _permanentFreeSoulsEventOwned;
    private float _nextPermanentFreeSoulsAttemptAt;
    private int _ragePillsThisEvent;
    private bool _pillAttemptedThisRage;
    private RageModeManager.RageModeStates _previousRageState = RageModeManager.RageModeStates.NotActive;
    private double _rageWindowDuration;
    private double _rageWindowElapsed;
    private float _lastRageWindowClockAt = -1f;
    private MaterialNeed _materialNeed;
    private SoulMapEfficiency _idleSoulMap;
    private string _lastIdleSoulFocus;
    private QuestPlan _questPlan;
    private IReadOnlyList<QuestPlan> _parallelQuestPlans = Array.Empty<QuestPlan>();
    private readonly HashSet<int> _parallelAppliedPlanIds = new();
    private string _lastParallelQuestKey;
    private int _questRagePlanId;
    private int _ragePillsThisQuest;
    private bool _materialRecoveryWasActive;
    private string _lastExecutionPriorityKey;
    private string _portalDeferralKey;
    private string _materialLingerKey;
    private float _materialLingerUntil;
    private float _portalQuestProtectionUntil = -1000f;
    private float _nextPortalProtectionLogAt;
    private string _activeStaffSupplyDeferralKey;
    private float _nextActiveStaffSupplyDeferralLogAt;
    private float _nextMaterialQuestCapacityCheckAt;
    private float _nextMaterialQuestCapacityLogAt;
    private float _nextRandomBoxStaffCheckAt;
    private string _materialPlanEndReason;
    private float _nextCraftQuestEggAttemptAt;
    private float _nextCraftQuestEggLogAt;
    private float _chestPreparationLatchedUntil;
    private int _lastCharacterSwitchQuestId;
    private float _nextCharacterSwitchCheckAt;
    private float _nextCharacterSwitchErrorLogAt;
    private bool _quitSuspended;

    public void Awake()
    {
        if (Current != null && Current != this)
        {
            SmartLog.Warning(SmartLogCategory.Runtime,
                "Rejected a duplicate runtime component after a scene transition; the existing controller remains authoritative.");
            UnityEngine.Object.Destroy(this);
            return;
        }

        Current = this;
        _materials = new MaterialPlanner(_catalog);
        _quests = new QuestDirector(_catalog);
        _sceneReadyAt = Time.unscaledTime;
        _catalog.ResolveAll(forceLog: true);
        SmartLog.Debug(SmartLogCategory.Runtime,
            $"Runtime component ready: enabled={Plugin.Config.Enabled.Value}. Compatibility checks never change other mods.");
    }

    public void OnDestroy()
    {
        if (Current != this) return;
        ReleaseQuestRerollEligibility();
        ResetSmartCombat(clearCache: true);
        if (Current == this) Current = null;
    }

    public void OnApplicationQuit()
    {
        if (Current == this) Plugin.SuspendForApplicationQuit();
    }

    public void Update()
    {
        if (Current != this) return;
        if (Plugin.IsApplicationQuitting || GameContracts.IsGameQuitRequested())
        {
            SuspendForGameQuit();
            return;
        }
        if (_quitSuspended)
        {
            _quitSuspended = false;
            _sceneReadyAt = Time.unscaledTime;
            _nextTick = 0;
            InvalidateMaterialPlan();
            _quests?.RequestImmediateReplan("game exit was cancelled");
            SmartLog.Info(SmartLogCategory.Runtime,
                "The game exit flow was cancelled; automation will resume after the normal scene-ready grace period.");
        }
        SmartSettingsUi.Tick();
        HandleKeys();
        MaintainQuestRerollEligibility();
        MaintainPortalCooldownReset();
        if (SmartSettingsUi.IsOpen)
        {
            ResetGiantRapidAttack();
            ResetSmartCombat();
            return;
        }
        if (!Plugin.Config.Enabled.Value || _emergencyStopped)
        {
            ResetGiantRapidAttack();
            ResetSmartCombat();
            return;
        }

        if (!PaidSoulsBurstOwnsControl) _quests?.TickActiveTime();

        try { HandleGiantRapidAttack(); }
        catch (Exception ex)
        {
            ResetGiantRapidAttack();
            if (Time.unscaledTime >= _nextGiantAttackErrorLogAt)
            {
                _nextGiantAttackErrorLogAt = Time.unscaledTime + 30f;
                SmartLog.Warning(SmartLogCategory.Combat,
                    $"Rapid Giant attack failed; retrying later: {ex.GetType().Name}: {ex.Message}");
            }
        }

        try { HandleSmartCombat(); }
        catch (Exception ex) { SuspendCombatAfterError(Time.unscaledTime, $"{ex.GetType().Name}: {ex.Message}"); }

        try { HandleLiveRandomBoxStaff(); }
        catch (Exception ex)
        {
            if (Plugin.Config.DebugLogging.Value)
                SmartLog.Debug(SmartLogCategory.Event,
                    $"Live Random Box staff check failed; retrying next cycle: {ex.GetType().Name}: {ex.Message}");
        }

        if (Time.unscaledTime < _nextTick) return;
        _nextTick = Time.unscaledTime + Math.Max(0.2f, Plugin.Config.CheckIntervalSeconds.Value);

        try { Tick(); }
        catch (Exception ex)
        {
            _emergencyStopped = true;
            SmartLog.Error(SmartLogCategory.Runtime,
                $"Unhandled strategy exception triggered an emergency stop. Review the log, then press F9 to recover: {ex}");
        }
    }

    internal void SuspendForGameQuit()
    {
        if (_quitSuspended) return;
        _quitSuspended = true;
        ReleaseQuestRerollEligibility();
        ResetGiantRapidAttack();
        ResetSmartCombat(clearCache: true);
        _questPlan = null;
        _parallelQuestPlans = Array.Empty<QuestPlan>();
        _parallelAppliedPlanIds.Clear();
        _materialNeed = null;
        SmartLog.Info(SmartLogCategory.Runtime,
            "Game exit upload started; all strategy mutations are suspended until the process exits or the exit is cancelled.");
    }

    private void HandleGiantRapidAttack()
    {
        if (!Plugin.Config.GiantRapidAttackEnabled.Value || GameState.current != GameStates.BattleMode)
        {
            ResetGiantRapidAttack();
            return;
        }

        var now = Time.unscaledTime;
        var player = PlayerMovement.instance;
        var battle = BattleModeManager.instance;
        var enemy = battle?.currentFightingEnemy;
        if (player == null || battle == null || enemy == null || !battle.IsFighting())
        {
            ResetGiantRapidAttack();
            return;
        }

        if (!QuestDirector.IsStandardAvailableMap(MapController.instance?.selectedMap))
        {
            ResetGiantRapidAttack();
            return;
        }

        if (!_giantBattleLogged)
        {
            _giantBattleLogged = true;
            if (BattleModeAttackBridge.HasObservedRange && !_giantAttackInterfaceLogged)
            {
                _giantAttackInterfaceLogged = true;
                SmartLog.Debug(SmartLogCategory.Combat,
                    $"Reusing captured native Giant-attack rangeX={BattleModeAttackBridge.ObservedRangeX:0.###}.");
            }
            else if (!BattleModeAttackBridge.HasObservedRange && !_giantCapturePromptLogged)
            {
                _giantCapturePromptLogged = true;
                SmartLog.Debug(SmartLogCategory.Combat,
                    "Detected the first native Battle Mode encounter; one native attack will capture the real BMAttack parameter.");
            }
        }

        if (now < _nextGiantAttackAt) return;
        if (!BattleModeAttackBridge.HasObservedRange)
        {
        // PlayerMovement.Attack has a native cooldown but forwards the version-correct rangeX to BMAttack. After one
        // capture, call the battle interface directly without ordinary input cooldown.
            player.Attack();
            _nextGiantAttackAt = now + Math.Clamp(Plugin.Config.GiantRapidAttackIntervalSeconds.Value, 0.01f, 0.5f);
            return;
        }

        if (!_giantAttackInterfaceLogged)
        {
            _giantAttackInterfaceLogged = true;
            SmartLog.Debug(SmartLogCategory.Combat,
                $"Captured native rangeX={BattleModeAttackBridge.ObservedRangeX:0.###}; using BMAttack at " +
                $"{Math.Max(0.01f, Plugin.Config.GiantRapidAttackIntervalSeconds.Value):0.###}s intervals.");
        }
        try
        {
            BattleModeAttackBridge.SyntheticCall = true;
            battle.BMAttack(BattleModeAttackBridge.ObservedRangeX);
        }
        finally
        {
            BattleModeAttackBridge.SyntheticCall = false;
        }
        _nextGiantAttackAt = now + Math.Clamp(Plugin.Config.GiantRapidAttackIntervalSeconds.Value, 0.01f, 0.5f);
    }

    private void ResetGiantRapidAttack()
    {
        _giantBattleLogged = false;
        _nextGiantAttackAt = 0;
        BattleModeAttackBridge.ResetEncounter();
    }

    private void HandleHordeRage()
    {
        var rage = RageModeManager.instance;
        if (rage == null || rage.currentState != RageModeManager.RageModeStates.NotActive) return;
        if (rage.currentCd <= 0.01)
            TryActivateReadyRage("Horde event; natural cooldown only", force: true);
    }

    internal bool PrepareForChestHunt(string trigger = "unknown trigger")
    {
        if (!Plugin.Config.Enabled.Value || _emergencyStopped || !Plugin.Config.CraftBeforeChestHunt.Value)
            return false;
        var gameState = GameState.current;
        if (gameState != GameStates.RunnerMode && gameState != GameStates.RageMode && gameState != GameStates.ChestHunt)
            return false;

        var soulEvent = ReadSoulsEvent();
        if (!soulEvent.Active && !Plugin.Config.BoostChestHuntWithoutSoulsEvent.Value) return false;

        var now = Time.unscaledTime;
        if (now < _chestPreparationLatchedUntil) return true;
        _chestPreparationLatchedUntil = now + RuntimeCadence.ChestPreparationLatchSeconds;

        var threshold = Math.Max(1f, Plugin.Config.ChestHuntMinimumEffectSeconds.Value);
        var crafted = new List<string>();
        if (Plugin.Config.SoulsSpyglassEnabled.Value &&
            CanCraftWithoutReducingPaidPillStock(SmartItem.SoulsSpyglass) &&
            NeedsCoverage(SmartItem.SoulsSpyglass, threshold) &&
            TryCraft(SmartItem.SoulsSpyglass, Plugin.Config.SoulsSpyglassReserveUses.Value,
                "Chest Hunt Souls bonus; rare-material reserve ignored", ignoreGlobalInterval: true,
                allowReserveBorrow: true, ignoreMaterialReserve: true))
            crafted.Add("Souls Spyglass");

        var spyglass = _catalog.Get(SmartItem.SoulsSpyglass);
        var mayStackCompass = Plugin.Config.StackCompassWithSpyglassForChestHunt.Value || spyglass == null || spyglass.currentTime < threshold;
        if (mayStackCompass && Plugin.Config.SoulsCompassEnabled.Value &&
            CanCraftWithoutReducingPaidPillStock(SmartItem.SoulsCompass) &&
            NeedsCoverage(SmartItem.SoulsCompass, threshold) &&
            TryCraft(SmartItem.SoulsCompass, Plugin.Config.SoulsCompassReserveUses.Value,
                "Chest Hunt Souls bonus; rare-material reserve ignored", ignoreGlobalInterval: true,
                allowReserveBorrow: true, ignoreMaterialReserve: true))
            crafted.Add("Souls Compass");

        if (crafted.Count > 0)
            SmartLog.Info(SmartLogCategory.Event,
                $"Chest Hunt preparation completed: trigger={trigger}, activated={string.Join(" + ", crafted)}.");
        else
            SmartLog.Debug(SmartLogCategory.Event,
                $"Chest Hunt preparation made no new craft: trigger={trigger}, Spyglass={EffectSeconds(SmartItem.SoulsSpyglass):0.0}s, " +
                $"Compass={EffectSeconds(SmartItem.SoulsCompass):0.0}s, threshold={threshold:0.0}s.");
        return true;
    }

    private void Tick()
    {
        TrackMapChange();
        var stable = IsRunnerStable();
        EnsurePermanentClientBonuses(stable);
        EnsureEndgamePermanentPaidBonuses(stable);
        var soulEvent = ReadSoulsEvent();
        var hordeActive = ReadHordeActive();
        TrackSoulEvent(soulEvent);
        TrackHordeEvent(hordeActive);
        TrackRageCycle();
        if (HandlePaidSoulsBurstStrategy(stable)) return;
        var qualifyingSoulEvent = soulEvent.Active &&
                                  soulEvent.Multiplier >= Math.Max(1, Plugin.Config.MinimumSoulsMultiplier.Value);
        if (qualifyingSoulEvent && !CanExecuteSoulsBurst(stable))
        {
            if (!_soulBurstDeferred)
            {
                _soulBurstDeferred = true;
                if (!_soulDeferredLogWritten)
                {
                    _soulDeferredLogWritten = true;
                    var mapName = MapController.instance?.selectedMap?.name ?? "unknown";
                    SmartLog.Debug(SmartLogCategory.Event,
                        $"Souls event deferred in state={GameState.current}, map={mapName}; crafting and Rage activation are not currently safe.");
                }
            }
            return;
        }
        if (!stable) return;

        if (hordeActive && Plugin.Config.RageDuringHordeEventEnabled.Value)
            HandleHordeRage();

        if (qualifyingSoulEvent)
        {
            if (_soulBurstDeferred)
            {
                _soulBurstDeferred = false;
                if (!_soulRecoveryLogWritten)
                {
                    _soulRecoveryLogWritten = true;
                    SmartLog.Debug(SmartLogCategory.Event,
                        $"Souls event processing resumed on map={MapController.instance?.selectedMap?.name ?? "unknown"}, remaining={soulEvent.SecondsLeft:0.0}s.");
                }
            }
            if (HandleSoulsBurst(soulEvent)) return;
        }

        if (PrepareQuestPlan()) return;

        if (HandleRequiredCharacterTask()) return;

        if (TryRelieveBlockedMaterialQuestCapacity()) return;

        if (TryOpenDragonEggForCraftQuest(_quests?.FindActiveCraftTemporaryItemsQuest())) return;

        ApplyParallelQuestBoosters();

        // Map-agnostic Random Box quests progress naturally during material farming, so their booster may run in parallel
        // rather than being blocked by material priority. Handle only same-map box boosters; TryCraft still owns reserves.
        if (TryApplyRandomBoxQuestBoosters(_questPlan)) return;

        if (HandleMaterialRecovery(soulEvent, hordeActive))
        {
            _quests.HoldPlanDuringMaterialRecovery();
            return;
        }

        if (HandleIdleBestSoulsMap()) return;

        HandleQuestAutomation();
    }

    private bool HandleSoulsBurst(SoulsEventState soulEvent)
    {
        var rage = RageModeManager.instance;
        if (rage != null && rage.currentState != RageModeManager.RageModeStates.NotActive &&
            Plugin.Config.RagePillEnabled.Value && !_pillAttemptedThisRage && rage.currentCd > 0.01 &&
            soulEvent.SecondsLeft >= Math.Max(1, Plugin.Config.MinimumSecondsForAnotherRage.Value) &&
            CanSpendRagePillForSoulEvent(soulEvent))
        {
            _pillAttemptedThisRage = true;
            var before = rage.currentCd;
            if (TryCraft(SmartItem.RagePill, Plugin.Config.RagePillReserveUses.Value,
                    $"Souls {soulEvent.Multiplier:0.#}x Rage extension",
                    allowReserveBorrow: _ragePillsThisEvent == 0))
            {
                _ragePillsThisEvent++;
                SmartLog.Info(SmartLogCategory.Event,
                    $"Used a Rage Pill: cooldown {before:0.0}s -> {rage.currentCd:0.0}s; event pills={_ragePillsThisEvent}/{Math.Max(0, Plugin.Config.MaximumRagePillsPerEvent.Value)}.");
                return true;
            }
        }

        // A Souls event may appear during cooldown after the previous Rage ends. Clear cooldown with a pill first;
        // otherwise neither pre-clearing during Rage nor direct activation at zero cooldown applies and the event is missed.
        if (rage != null && rage.currentState == RageModeManager.RageModeStates.NotActive &&
            Plugin.Config.RagePillEnabled.Value && rage.currentCd > 0.01 &&
            soulEvent.SecondsLeft >= Math.Max(
                Math.Max(1, Plugin.Config.MinimumSecondsToStartRage.Value),
                Math.Max(1, Plugin.Config.MinimumSecondsForAnotherRage.Value)) &&
            CanSpendRagePillForSoulEvent(soulEvent))
        {
            var before = rage.currentCd;
            if (TryCraft(SmartItem.RagePill, Plugin.Config.RagePillReserveUses.Value,
                    $"Souls {soulEvent.Multiplier:0.#}x Rage activation",
                    allowReserveBorrow: _ragePillsThisEvent == 0))
            {
                _ragePillsThisEvent++;
                SmartLog.Info(SmartLogCategory.Event,
                    $"Prepared Souls-event Rage with a pill: cooldown {before:0.0}s -> {rage.currentCd:0.0}s; " +
                    $"event pills={_ragePillsThisEvent}/{Math.Max(0, Plugin.Config.MaximumRagePillsPerEvent.Value)}.");
                return true;
            }
        }

        if (soulEvent.SecondsLeft >= Math.Max(1, Plugin.Config.MinimumSoulsEventSeconds.Value))
        {
            var spyglassTarget = Math.Max(soulEvent.SecondsLeft + 5, Plugin.Config.SoulsSpyglassRefreshMinutes.Value * 60);
            if (Plugin.Config.SoulsSpyglassEnabled.Value && NeedsCoverage(SmartItem.SoulsSpyglass, spyglassTarget) &&
                TryCraft(SmartItem.SoulsSpyglass, Plugin.Config.SoulsSpyglassReserveUses.Value, $"Souls {soulEvent.Multiplier:0.#}x")) return true;

            if (Plugin.Config.SoulsCompassEnabled.Value && NeedsCoverage(SmartItem.SoulsCompass, soulEvent.SecondsLeft + 5) &&
                TryCraft(SmartItem.SoulsCompass, Plugin.Config.SoulsCompassReserveUses.Value, $"Souls {soulEvent.Multiplier:0.#}x")) return true;

            var hasGiant = CurrentMapHasGiant();
            if (hasGiant && Plugin.Config.ShardsNecklaceEnabled.Value && NeedsCoverage(SmartItem.ShardsNecklace, soulEvent.SecondsLeft + 5) &&
                TryCraft(SmartItem.ShardsNecklace, Plugin.Config.ShardsNecklaceReserveUses.Value, $"Souls {soulEvent.Multiplier:0.#}x + Giant map")) return true;

            if (hasGiant && Plugin.Config.CriticalSwordEnabled.Value &&
                soulEvent.Multiplier >= Math.Max(1, Plugin.Config.CriticalSwordMinimumMultiplier.Value) &&
                NeedsCoverage(SmartItem.CriticalSword, soulEvent.SecondsLeft + 5) &&
                TryCraft(SmartItem.CriticalSword, Plugin.Config.CriticalSwordReserveUses.Value, $"High Souls multiplier {soulEvent.Multiplier:0.#}x")) return true;
        }

        if (Plugin.Config.ControlRageActivation.Value && rage != null &&
            rage.currentState == RageModeManager.RageModeStates.NotActive && rage.currentCd <= 0.01 &&
            soulEvent.SecondsLeft >= Math.Max(1, Plugin.Config.MinimumSecondsToStartRage.Value))
        {
            rage.Activate();
            SmartLog.Info(SmartLogCategory.Event,
                $"Activated Rage during a {soulEvent.Multiplier:0.#}x Souls event with {soulEvent.SecondsLeft:0.0}s remaining.");
            return true;
        }
        return false;
    }

    private bool CanSpendRagePillForSoulEvent(SoulsEventState soulEvent)
    {
        if (_ragePillsThisEvent >= Math.Max(0, Plugin.Config.MaximumRagePillsPerEvent.Value)) return false;
        if (_ragePillsThisEvent == 0) return true;
        return soulEvent.Multiplier >= Math.Max(
            Math.Max(1, Plugin.Config.MinimumSoulsMultiplier.Value),
            Math.Max(1, Plugin.Config.SecondRagePillMinimumSoulsMultiplier.Value));
    }

    private bool HandleMaterialRecovery(SoulsEventState soulEvent, bool hordeActive)
    {
        if (!Plugin.Config.AutomaticMaterialTravel.Value)
        {
            EndMaterialRecoveryPriority("Material travel is disabled");
            return false;
        }

        if (Time.unscaledTime >= _nextMaterialScan)
        {
            _nextMaterialScan = Time.unscaledTime + Math.Max(2f, Plugin.Config.MaterialScanIntervalSeconds.Value);
            var previousNeed = _materialNeed;
            _materialPlanEndReason = null;
            var completedOrInvalid = MaterialNeedRequiresReplan(previousNeed);
        // The five-second poll reads lightweight state only. Deep cycles recalculate idle, same-map, and direct quest
        // supply from current progress, preventing oversupply after incidental progress without restoring a full pattern
        // scan every five seconds.
            var deepReplanDue = (previousNeed == null || previousNeed.Opportunistic ||
                                 previousNeed.QuestPlanId != 0) &&
                                Time.unscaledTime >= _nextMaterialDeepReplan;
            if (_materialPlanDirty || completedOrInvalid || deepReplanDue)
            {
                var nextNeed = _materials.FindMostImportantNeed(previousNeed, _questPlan,
                    PortalQuestProtectionActive(), ParallelRageSupplyPlan(),
                    _quests?.HasActiveDailyTasks == true);
                _materialNeed = ShouldKeepDedicatedQuestSupply(previousNeed, nextNeed) ||
                                TryKeepMaterialNeedForBoosterLinger(previousNeed, nextNeed)
                    ? previousNeed
                    : nextNeed;
                _materialPlanDirty = false;
                _nextMaterialDeepReplan = Time.unscaledTime +
                    Math.Max(10f, Plugin.Config.MaterialDeepReplanIntervalSeconds.Value);
            }
        }
        if (_materialNeed == null)
        {
            EndMaterialRecoveryPriority(_materialPlanEndReason ?? "Material stock reached the current strategy threshold");
            _materialPlanEndReason = null;
            return false;
        }

        // A critical unlock allows preemption only by Whetstone supply or on-demand supply serving that quest directly.
        if (_questPlan?.StrategicUnlockBonus > 0 && _materialNeed.Priority > 0 &&
            _materialNeed.QuestPlanId != _questPlan.InstanceId)
        {
            if (Plugin.Config.DebugLogging.Value)
                SmartLog.Debug(SmartLogCategory.Planning,
                    $"Strategic unlock quest is deferring routine material supply; material={_materialNeed.Material.name}.");
            _materialNeed = null;
            EndMaterialRecoveryPriority("Strategic unlock quest has priority");
            return false;
        }

        // When a quest staff is active, spend its paid window killing first. Recalculate supply from latest progress after
        // coverage ends, avoiding an immediate departure to replace materials consumed by the craft.
        var materialOwnerPlan = FindExecutionPlan(_materialNeed.QuestPlanId) ?? _questPlan;
        if (!Plugin.EndgameModeActive &&
            ShouldDeferQuestSupplyForActiveStaff(_materialNeed, materialOwnerPlan, out var staffSeconds))
        {
            EndMaterialRecoveryPriority($"Quest staff has {staffSeconds:0}s coverage; kill before resupplying");
            LogActiveStaffSupplyDeferral(_materialNeed, materialOwnerPlan, staffSeconds);
            return false;
        }
        _activeStaffSupplyDeferralKey = null;

        var opportunistic = _materialNeed.Opportunistic && _questPlan != null &&
                            _materialNeed.QuestPlanId == _questPlan.InstanceId &&
                            MapEfficiencyEstimator.SameMap(_materialNeed.Map, _questPlan.TargetMap);
        if (opportunistic) EndMaterialRecoveryPriority("Switching to opportunistic same-map quest supply");
        else LogMaterialRecoveryPriority();

        // A confirmed directly farmable material gap owns highest priority; do not switch to quests while waiting for
        // stability, event end, or Portal cooldown.
        if (!Plugin.EndgameModeActive && !opportunistic &&
            ((IsQualifyingSoulEvent(soulEvent) && !_permanentFreeSoulsEventOwned) || hordeActive)) return true;

        var rage = RageModeManager.instance;
        if (!Plugin.EndgameModeActive && !opportunistic && rage != null &&
            rage.currentState != RageModeManager.RageModeStates.NotActive) return true;
        var chest = ChestHuntManager.instance;
        if (!opportunistic && chest != null && chest.eventInProgress) return true;

        var materialName = DisplayName(_materialNeed.Material);
        if (MapEfficiencyEstimator.SameMap(_materialNeed.Map, MapController.instance?.selectedMap))
        {
            var focus = $"{materialName}@{MapController.instance.selectedMap?.localizedName}";
            if (_lastMaterialFocus != focus)
            {
                _lastMaterialFocus = focus;
                SmartLog.Info(SmartLogCategory.Material,
                    $"{(opportunistic ? "Opportunistic supply" : "Material supply")}: material={_materialNeed.Material.name}, " +
                    $"progress={_materialNeed.Current:0}/{_materialNeed.Desired:0}, targetEnemiesPerPattern={_materialNeed.TargetEnemiesPerPattern:0.##}.");
            }
            // Linger after target only while existing bonuses expire; renewing them would create a loop that never leaves.
            if (!opportunistic && IsLingeringFor(_materialNeed))
            {
                TryActivateReadyRage($"Material bonus linger for {materialName}");
                return true;
            }
            if (_materialNeed.GiantDrop && Plugin.Config.ShardsNecklaceEnabled.Value &&
                NeedsCoverage(SmartItem.ShardsNecklace, 20) &&
                TryCraft(SmartItem.ShardsNecklace, Plugin.Config.ShardsNecklaceReserveUses.Value,
                    $"Giant material {materialName}", allowReserveBorrow: true))
                return !opportunistic;
            var whetstoneBreakEven = Math.Max(1, Plugin.Config.WhetstoneMinimumExpectedEligibleKills.Value);
            if (Plugin.Config.WhetstoneDuringMaterialRecovery.Value && _materialNeed.ExpectedEligibleKills >= whetstoneBreakEven &&
                NeedsCoverage(SmartItem.Whetstone, 20) &&
                TryCraft(SmartItem.Whetstone, Plugin.Config.WhetstoneReserveUses.Value, $"Collect {materialName}",
                    allowReserveBorrow: true))
                return !opportunistic;
            if (opportunistic) return false;
            TryActivateReadyRage($"Material farming for {materialName}");
            return true;
        }

        _lastMaterialFocus = null;
        // Opportunistic supply never owns travel; let quest execution reach the same optimal quest map as planned.
        if (opportunistic) return false;
        // Startup protection restricts only cross-map Portal opening; same-map booster crafting and farming may start immediately.
        if (Time.unscaledTime < _sceneReadyAt + Math.Max(0, Plugin.Config.StartupGraceSeconds.Value)) return true;
        if (Time.unscaledTime - _portalRequestedAt < Math.Max(10, Plugin.Config.PortalRetrySeconds.Value)) return true;
        if (!CanOpenPortal(out var portal)) return true;
        _portalGroundReservationUntil = Time.unscaledTime + RuntimeCadence.PortalGroundReservationSeconds;
        if (!PlayerGroundedForPortal()) return true;
        if (_materialNeed.Map == null || !_materialNeed.Map.IsAvailable()) return true;

        portal.SpawnPortal(_materialNeed.Map, false);
        _portalRequestedAt = Time.unscaledTime;
        _portalGroundReservationUntil = 0;
        SmartLog.Info(SmartLogCategory.Portal,
            $"Opening Portal for material supply; currentMap={MapController.instance?.selectedMap?.name ?? "unknown"}, " +
            $"targetMap={_materialNeed.Map.name}, material={_materialNeed.Material.name}, " +
            $"progress={_materialNeed.Current:0}/{_materialNeed.Desired:0}, questMap={_questPlan?.TargetMap?.name ?? "any"}.");
        return true;
    }

    private bool ShouldDeferQuestSupplyForActiveStaff(MaterialNeed need, QuestPlan plan,
        out double secondsLeft)
    {
        secondsLeft = 0;
        if (need == null || need.Opportunistic || need.QuestPlanId == 0 || plan?.Quest == null ||
            need.QuestPlanId != plan.InstanceId || plan.Current >= plan.Goal || !IsKillQuest(plan.Type) ||
            plan.IsCollab) return false;

        // Rage kills use staffs only inside a Rage window; cooldown may still prepare pill and future staff materials.
        if (plan.Type == QuestType.KillEnemiesWithRageMode &&
            RageModeManager.instance?.currentState == RageModeManager.RageModeStates.NotActive) return false;

        secondsLeft = Math.Max(EffectSeconds(SmartItem.DeathwaveScepter),
            Math.Max(EffectSeconds(SmartItem.BidimensionalStaff),
                EffectSeconds(SmartItem.AlternateDimensionStaff)));
        return secondsLeft > 1;
    }

    private void LogActiveStaffSupplyDeferral(MaterialNeed need, QuestPlan plan, double secondsLeft)
    {
        var key = $"{plan.InstanceId}|{need.Material?.GetInstanceID() ?? 0}";
        if (string.Equals(_activeStaffSupplyDeferralKey, key, StringComparison.Ordinal) &&
            Time.unscaledTime < _nextActiveStaffSupplyDeferralLogAt) return;
        _activeStaffSupplyDeferralKey = key;
        _nextActiveStaffSupplyDeferralLogAt = Time.unscaledTime + 15f;
        SmartLog.Info(SmartLogCategory.Material,
            $"Active quest staff window is deferring material travel; remaining={secondsLeft:0.0}s, " +
            $"material={need.Material.name}, targetMap={need.Map?.name ?? "unknown"}. The plan will be recalculated afterward.");
    }

    private bool MaterialNeedRequiresReplan(MaterialNeed need)
    {
        if (need == null) return false;
        if (_quests?.HasActiveDailyTasks == true && need.QuestPlanId == 0 &&
            need.Purpose?.Contains("Idle surplus material supply", StringComparison.Ordinal) == true)
        {
            _materialPlanEndReason = "An active Daily was detected; pause lowest-priority material filling";
            return true;
        }
        if (need.Material == null || need.Map == null)
        {
            _materialPlanEndReason = "The previous material target is stale";
            return true;
        }
        if (need.Opportunistic && (_questPlan == null || need.QuestPlanId != _questPlan.InstanceId ||
            !MapEfficiencyEstimator.SameMap(need.Map, _questPlan.TargetMap)))
        {
            _materialPlanEndReason = $"Opportunistic supply no longer matches current quest {_questPlan?.Name ?? "none"}";
            return true;
        }
        if (need.QuestPlanId != 0 && FindExecutionPlan(need.QuestPlanId) == null)
        {
            _materialPlanEndReason = $"Supply plan '{need.Purpose}' was preempted by higher-priority quest {_questPlan?.Name ?? "none"}";
            return true;
        }
        try
        {
            if (need.Material.locked || !need.Map.IsAvailable())
            {
                _materialPlanEndReason = "Material or target map became unavailable";
                return true;
            }
            var completed = need.Material.amount + 0.0001 >= need.Desired && !IsLingeringFor(need);
            if (completed) _materialPlanEndReason = $"{DisplayName(need.Material)} reached planned stock";
            return completed;
        }
        catch
        {
            _materialPlanEndReason = "Unable to read material-target state; replanning";
            return true;
        }
    }

    private bool TryRelieveBlockedMaterialQuestCapacity()
    {
        if (!Plugin.Config.MaterialQuestCapacityReliefEnabled.Value || _quests == null) return false;
        var now = Time.unscaledTime;
        if (now < _nextMaterialQuestCapacityCheckAt) return false;
        _nextMaterialQuestCapacityCheckAt = now +
            Math.Clamp(Plugin.Config.MaterialQuestCapacityCheckIntervalSeconds.Value, 5f, 120f);

        var quest = _quests.FindActiveMaterialCollectionQuest();
        if (quest == null || MapEfficiencyEstimator.HasCollectibleMaterialSpace()) return false;
        if (now - _lastCraftAt < 0.45f) return false;

        foreach (var kind in MaterialQuestCapacityReliefItems())
        {
            var item = _catalog.Get(kind);
            if (!CanCraftForMaterialCapacity(item, out var freedMaterial, out var freedAmount)) continue;
            if (!TryCraft(kind, 0,
                    $"Free capacity for a full-stock material quest: {QuestLabel(quest)}; release {freedMaterial} capacity {freedAmount:0}",
                    allowReserveBorrow: true)) continue;
            SmartLog.Info(SmartLogCategory.Material,
                $"Material quest capacity recovered by crafting {kind}; freedMaterial={freedMaterial}, freedAmount={freedAmount:0}.");
            return true;
        }

        if (now >= _nextMaterialQuestCapacityLogAt)
        {
            _nextMaterialQuestCapacityLogAt = now + 300f;
            SmartLog.Warning(SmartLogCategory.Material,
                $"Material quest is blocked because every farmable material is full and no unlocked craftable can safely free capacity; " +
                $"questInstanceId={quest.GetInstanceID()}, retryIn=300s.");
        }
        return false;
    }

    private static IEnumerable<SmartItem> MaterialQuestCapacityReliefItems()
    {
        // Whetstone both creates room and raises subsequent material yield. Kill staffs follow because they
        // can free capped recipe materials while accelerating the already selected combat objective.
        yield return SmartItem.Whetstone;
        yield return SmartItem.DeathwaveScepter;
        yield return SmartItem.BidimensionalStaff;
        yield return SmartItem.AlternateDimensionStaff;
        yield return SmartItem.ShardsNecklace;
        yield return SmartItem.SoulsCompass;
        yield return SmartItem.RandomStone;
        yield return SmartItem.NecklaceOfCollectables;
        yield return SmartItem.RandomBoxStaff;
        yield return SmartItem.CriticalSword;
        yield return SmartItem.SoulsSpyglass;
        yield return SmartItem.Specialization;
        yield return SmartItem.KeyManifest;
    }

    private static bool CanCraftForMaterialCapacity(TemporaryCraftableItem item,
        out string freedMaterial, out double freedAmount)
    {
        freedMaterial = null;
        freedAmount = 0;
        if (item == null) return false;
        try
        {
            if (!item.TabVisible() || !item.ExtraCondition() || item.HowManyCanCraft() < 1) return false;
            var requirements = item.GetRequirements();
            if (requirements == null) return false;
            for (var i = 0; i < requirements.Length; i++)
            {
                var requirement = requirements[i];
                var material = requirement?.material;
                if (material == null || requirement.divinityPoints || requirement.amount <= 0 || material.locked)
                    continue;
                var capacity = material.GetMaxAmount();
                if (capacity <= 0 || material.amount + 0.0001 < capacity) continue;
                if (MapEfficiencyEstimator.MaterialProfiles(material).Count == 0) continue;
                freedMaterial = DisplayName(material);
                freedAmount = requirement.amount;
                return true;
            }
        }
        catch { return false; }
        return false;
    }

    private static string QuestLabel(Quest quest) =>
        string.IsNullOrWhiteSpace(quest?.localizedName) ? quest?.name ?? "Unknown Material Quest" : quest.localizedName;

    private void InvalidateMaterialPlan()
    {
        _materialPlanDirty = true;
        _nextMaterialScan = 0;
    }

    private void RequestImmediateRoutingReplan(string reason)
    {
        _materialPlanDirty = true;
        _nextMaterialScan = 0f;
        _nextMaterialDeepReplan = 0f;
        _nextIdleSoulMapScan = 0f;
        _portalDeferralKey = null;
        _quests?.RequestImmediateReplan(reason);
        _questPlan = null;
    }

    private bool HandleIdleBestSoulsMap()
    {
        if (!Plugin.Config.AutomaticMaterialTravel.Value || !Plugin.Config.IdleSurplusMaterialFarming.Value ||
            !Plugin.Config.IdleBestSoulsMapTravel.Value || _materialNeed != null ||
            MaterialPlanner.BlocksIdleSurplusFarming(_questPlan) || PortalQuestProtectionActive())
        {
            _lastIdleSoulFocus = null;
            return false;
        }

        if (Time.unscaledTime >= _nextIdleSoulMapScan)
        {
            _nextIdleSoulMapScan = Time.unscaledTime +
                Math.Max(10f, Plugin.Config.MaterialDeepReplanIntervalSeconds.Value);
            _idleSoulMap = MapEfficiencyEstimator.BestForSouls();
        }
        if (_idleSoulMap?.Map == null) return false;

        var target = _idleSoulMap.Map;
        var targetId = target.id ?? target.name;
        if (MapEfficiencyEstimator.SameMap(target, MapController.instance?.selectedMap))
        {
            if (!string.Equals(_lastIdleSoulFocus, targetId, StringComparison.Ordinal))
            {
                _lastIdleSoulFocus = targetId;
                SmartLog.Info(SmartLogCategory.Planning,
                    $"Idle route selected the highest expected Souls map; map={target.name}, " +
                    $"soulsPerPattern={_idleSoulMap.SoulsPerPattern:0.###E+0}, eligiblePatterns={_idleSoulMap.EligiblePatterns}.");
            }
        return false; // Map-agnostic passive quests may still craft boosters and progress opportunistically.
        }

        _lastIdleSoulFocus = null;
        return TryOpenQuestPortal(target,
            $"Highest expected Souls route after all materials are full ({_idleSoulMap.SoulsPerPattern:0.###E+0} Souls/pattern)",
            logType: "Idle route");
    }

    private bool TryKeepMaterialNeedForBoosterLinger(MaterialNeed previous, MaterialNeed next)
    {
        if (Plugin.EndgameModeActive && Plugin.Config.PermanentTimedCraftableEffectsEnabled.Value)
        {
            ClearMaterialLinger();
            return false;
        }
        if (previous?.Material == null || previous.Opportunistic || !Plugin.Config.MaterialBoosterLingerEnabled.Value)
        {
            ClearMaterialLinger();
            return false;
        }

        double amount;
        double capacity;
        try
        {
            amount = previous.Material.amount;
            capacity = previous.Material.GetMaxAmount();
        }
        catch
        {
            ClearMaterialLinger();
            return false;
        }

        if (amount + 0.0001 < previous.Desired || capacity <= 0 || amount + 0.0001 >= capacity ||
            !MapEfficiencyEstimator.SameMap(previous.Map, MapController.instance?.selectedMap) ||
            next != null ||
            (_questPlan?.StrategicUnlockBonus > 0 && previous.QuestPlanId != _questPlan.InstanceId))
        {
            ClearMaterialLinger();
            return false;
        }

        var remaining = MaterialBoosterSecondsLeft(previous);
        var maximum = Math.Max(0, Plugin.Config.MaterialBoosterLingerMaximumSeconds.Value);
        var key = $"{previous.Material.GetInstanceID()}|{previous.Map?.id ?? previous.Map?.name}";
        if (!string.Equals(_materialLingerKey, key, StringComparison.Ordinal))
        {
            if (remaining <= 1 || maximum <= 0) return false;
            _materialLingerKey = key;
            _materialLingerUntil = Time.unscaledTime + (float)Math.Min(remaining, maximum);
            SmartLog.Info(SmartLogCategory.Material,
                $"Material target reached; material={previous.Material.name}, amount={amount:0}/{previous.Desired:0}. " +
                $"Lingering for up to {Math.Min(remaining, maximum):0}s while existing drop bonuses remain active.");
        }

        if (Time.unscaledTime < _materialLingerUntil && remaining > 1 && amount + 0.0001 < capacity) return true;

        SmartLog.Info(SmartLogCategory.Material,
            $"Material bonus linger ended; material={previous.Material.name}, amount={amount:0}/{capacity:0}. Resuming planning.");
        ClearMaterialLinger();
        return false;
    }

    private bool ShouldKeepDedicatedQuestSupply(MaterialNeed previous, MaterialNeed next)
    {
        // A transient primary change from a short high-priority quest or background reroll cannot replace active dedicated
        // staff supply with a small same-map gap. Dedicated supply keeps execution while its quest remains in the composite
        // group and material is below target, then releases on completion, group exit, or target reached.
        if (previous?.Material == null || previous.Map == null || previous.Opportunistic ||
            previous.QuestPlanId == 0 || next?.Opportunistic != true ||
            FindExecutionPlan(previous.QuestPlanId) == null)
            return false;
        try
        {
            return !previous.Material.locked && previous.Map.IsAvailable() &&
                   previous.Material.amount + 0.0001 < previous.Desired;
        }
        catch { return false; }
    }

    private double MaterialBoosterSecondsLeft(MaterialNeed need)
    {
        var remaining = 0.0;
        if (Plugin.Config.WhetstoneDuringMaterialRecovery.Value)
            remaining = Math.Max(remaining, ItemSecondsLeft(SmartItem.Whetstone));
        if (need?.GiantDrop == true && Plugin.Config.ShardsNecklaceEnabled.Value)
            remaining = Math.Max(remaining, ItemSecondsLeft(SmartItem.ShardsNecklace));
        return remaining;
    }

    private double ItemSecondsLeft(SmartItem kind)
    {
        var item = _catalog.Get(kind);
        if (item == null) return 0;
        try { return Math.Max(0, item.currentTime); }
        catch { return 0; }
    }

    private bool IsLingeringFor(MaterialNeed need)
    {
        if (need?.Material == null || string.IsNullOrEmpty(_materialLingerKey)) return false;
        var key = $"{need.Material.GetInstanceID()}|{need.Map?.id ?? need.Map?.name}";
        return string.Equals(_materialLingerKey, key, StringComparison.Ordinal) &&
            Time.unscaledTime < _materialLingerUntil;
    }

    private void ClearMaterialLinger()
    {
        _materialLingerKey = null;
        _materialLingerUntil = 0;
    }

    private void LogMaterialRecoveryPriority()
    {
        var actual = MapController.instance?.selectedMap?.name ?? "unknown";
        var materialTarget = _materialNeed?.Map?.name ?? "unknown";
        var questTarget = _questPlan?.TargetMap?.name ?? "any";
        var key = $"{actual}|{materialTarget}|{questTarget}|{_materialNeed?.Material?.GetInstanceID() ?? 0}";
        _materialRecoveryWasActive = true;
        if (_lastExecutionPriorityKey == key) return;
        _lastExecutionPriorityKey = key;
        SmartLog.Info(SmartLogCategory.Planning,
            $"Execution priority changed to material supply; currentMap={actual}, materialMap={materialTarget}, questMap={questTarget}.");
    }

    private void EndMaterialRecoveryPriority(string reason)
    {
        if (!_materialRecoveryWasActive) return;
        _materialRecoveryWasActive = false;
        _lastExecutionPriorityKey = null;
        SmartLog.Info(SmartLogCategory.Planning,
            $"Material supply priority ended; resuming quest plan, targetMap={_questPlan?.TargetMap?.name ?? "any"}.");
    }

    private bool PrepareQuestPlan()
    {
        if (!Plugin.Config.QuestAutomationEnabled.Value)
        {
            _questPlan = null;
            _parallelQuestPlans = Array.Empty<QuestPlan>();
            _lastParallelQuestKey = null;
            return false;
        }

        if (_quests.TickMaintenance())
        {
            _questPlan = null;
            _parallelQuestPlans = Array.Empty<QuestPlan>();
            return true;
        }

        var previousPlanId = _questPlan?.InstanceId ?? 0;
        var previousTarget = _questPlan?.TargetMap;
        var previousJointCount = _questPlan?.JointQuestCount ?? 0;
        _questPlan = _quests.SelectPlan();
        _parallelQuestPlans = _quests.FindParallelPlans(_questPlan);
        LogParallelQuestGroup();
        if (_questPlan?.Quest == null) return false;

        if (IsWeeklyPortalPlan(_questPlan))
            ExtendPortalQuestProtection("Executing a Go Through Portals Weekly");

        var routeChanged = previousTarget != null || _questPlan.TargetMap != null
            ? !SameMap(previousTarget, _questPlan.TargetMap)
            : false;
        if (previousPlanId != _questPlan.InstanceId || routeChanged ||
            previousJointCount != _questPlan.JointQuestCount)
            InvalidateMaterialPlan();
        var ragePlanId = RageExecutionPlanId();
        if (_questRagePlanId != ragePlanId)
        {
            _questRagePlanId = ragePlanId;
            _ragePillsThisQuest = 0;
        }
        return false;
    }

    private void ApplyParallelQuestBoosters()
    {
        if (_parallelQuestPlans == null || _parallelQuestPlans.Count == 0) return;
        var currentMap = MapController.instance?.selectedMap;
        // Do not consume short items or Rage before the primary route arrives; compatible quests share the same window afterward.
        if (_questPlan?.TargetMap != null && !SameMap(_questPlan.TargetMap, currentMap)) return;

        QuestPlan rage = null;
        QuestPlan giant = null;
        QuestPlan material = null;
        QuestPlan critical = null;
        QuestPlan collectable = null;
        QuestPlan craft = null;
        QuestPlan randomStone = null;
        foreach (var plan in _parallelQuestPlans)
        {
            if (plan?.Quest == null || plan.Current >= plan.Goal) continue;
            if (plan.TargetMap != null && !SameMap(plan.TargetMap, currentMap)) continue;
        // Ordinary and specified kills count naturally on the current map and do not need duplicate checks. Choose one
        // representative quest per global temporary effect so execution cost stays bounded as quest count grows.
            if (rage == null && RequiresActiveRageControl(plan)) rage = plan;
            if (giant == null && plan.TargetsGiants) giant = plan;
            if (material == null && plan.Type == QuestType.GetMaterials) material = plan;
            if (critical == null && plan.Type == QuestType.DoCriticals) critical = plan;
            if (collectable == null && IsCollectableQuest(plan.Type)) collectable = plan;
            if (craft == null && plan.Type == QuestType.CraftTemporaryItems) craft = plan;
            if (randomStone == null && plan.Type is QuestType.HitRandomBoxes or QuestType.GetCoinsFromRandomBox)
                randomStone = plan;
        }

        _parallelAppliedPlanIds.Clear();
        ApplyParallelQuestBoosterOnce(rage);
        ApplyParallelQuestBoosterOnce(giant);
        ApplyParallelQuestBoosterOnce(material);
        ApplyParallelQuestBoosterOnce(critical);
        ApplyParallelQuestBoosterOnce(collectable);
        ApplyParallelQuestBoosterOnce(craft);
        ApplyParallelQuestBoosterOnce(randomStone);
    }

    private void ApplyParallelQuestBoosterOnce(QuestPlan plan)
    {
        if (plan?.Quest == null || !_parallelAppliedPlanIds.Add(plan.InstanceId)) return;
        ApplyQuestBoosters(plan, parallel: true);
    }

    private int RageExecutionPlanId()
    {
        if (RequiresActiveRageControl(_questPlan)) return _questPlan.InstanceId;
        if (_parallelQuestPlans == null) return 0;
        foreach (var plan in _parallelQuestPlans)
            if (RequiresActiveRageControl(plan)) return plan.InstanceId;
        return 0;
    }

    private static bool RequiresActiveRageControl(QuestPlan plan) => plan?.Quest != null &&
        (plan.Type is QuestType.KillEnemiesWithRageMode or QuestType.UseRageMode ||
         plan.RequiresRageForBowAutomation);

    private void LogParallelQuestGroup()
    {
        var names = _parallelQuestPlans?
            .Where(plan => plan?.Quest != null)
            .Select(plan => $"{plan.Name}({plan.Type})")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();
        var key = $"{_questPlan?.InstanceId ?? 0}|{string.Join(";", names)}";
        if (string.Equals(key, _lastParallelQuestKey, StringComparison.Ordinal)) return;
        _lastParallelQuestKey = key;
        if (names.Length == 0) return;
        SmartLog.Info(SmartLogCategory.Quest,
            $"Parallel quest group updated; backgroundQuestCount={names.Length}. Crafting, Rage, and compatible kills share the active route.");
    }

    private QuestPlan ParallelRageSupplyPlan()
    {
        if (_parallelQuestPlans == null) return null;
        return _parallelQuestPlans.FirstOrDefault(plan => plan?.Quest != null &&
            plan.Type == QuestType.KillEnemiesWithRageMode && plan.Current < plan.Goal);
    }

    private QuestPlan FindExecutionPlan(int instanceId)
    {
        if (instanceId == 0) return null;
        if (_questPlan?.InstanceId == instanceId && _questPlan.Current < _questPlan.Goal) return _questPlan;
        if (_parallelQuestPlans == null) return null;
        return _parallelQuestPlans.FirstOrDefault(plan => plan?.InstanceId == instanceId &&
                                                          plan.Current < plan.Goal);
    }

    private void HandleQuestAutomation()
    {
        if (!Plugin.Config.QuestAutomationEnabled.Value || _questPlan?.Quest == null)
        {
            TryActivateReadyRage("Ordinary farming");
            return;
        }

        var target = _questPlan.TargetMap;
        if (_questPlan.Type == QuestType.GoThroughPortals)
        {
            ExtendPortalQuestProtection("Preparing to open a Weekly Portal");
            if (TryOpenQuestPortal(FindAlternativeQuestMap(), "Weekly Go Through Portals",
                    Math.Max(1f, Plugin.Config.WeeklyPortalProtectionSeconds.Value)))
            {
                ExtendPortalQuestProtection("Weekly Portal opened");
                _quests.ReleasePlan("Weekly Portal opened; select the highest-value quest after arrival");
                _questPlan = null;
            }
            return;
        }
        if (target != null && !SameMap(target, MapController.instance?.selectedMap))
        {
            TryOpenQuestPortal(target, $"Quest {_questPlan.Name}");
            return;
        }

        ApplyQuestBoosters(_questPlan);
        TryActivateReadyRage($"Quest {_questPlan.Name}");
    }

    private bool TryActivateReadyRage(string reason, bool force = false)
    {
        if (!force && !Plugin.Config.UseRageWheneverReady.Value) return false;
        var rage = RageModeManager.instance;
        if (rage == null || rage.currentState != RageModeManager.RageModeStates.NotActive || rage.currentCd > 0.01)
            return false;
        try
        {
            rage.Activate();
            SmartLog.Info(SmartLogCategory.Event, "Rage cooldown is ready; activated Rage immediately.");
            return true;
        }
        catch (Exception ex)
        {
            if (Plugin.Config.DebugLogging.Value)
                SmartLog.Warning(SmartLogCategory.Event,
                    $"Failed to activate Rage; retrying on the next cycle. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void ApplyQuestBoosters(QuestPlan plan, bool parallel = false)
    {
        if (PortalQuestProtectionActive())
        {
            LogPortalQuestProtection("Pause temporary booster crafting for the current quest");
            return;
        }
        var seconds = Math.Max(10f, Plugin.Config.QuestBoosterTargetSeconds.Value);
        var reserve = Math.Max(0, Plugin.Config.QuestBoosterReserveUses.Value);
        var reason = $"{(parallel ? "Parallel quest" : "Quest")} {plan.Name}";
        var naturalOnlyDailyRage = plan.Source == QuestSource.Daily &&
                                   plan.Type == QuestType.KillEnemiesWithRageMode;

        if (plan.Type == QuestType.KillEnemiesWithRageMode)
        {
        // Staff duration advances in real time, so start the Rage window first or staffs expire while waiting for natural
        // cooldown or pill materials.
            var rage = RageModeManager.instance;
            if (rage?.currentState == RageModeManager.RageModeStates.NotActive && rage.currentCd > 0.01)
            {
            // The pill is the final step of a combined plan, not an immediate craft when materials become available.
            // Let dedicated supply finish, then activate every recommended staff before clearing cooldown.
                if (DedicatedQuestSupplyInProgress(plan) || StageRecommendedStaffBeforeRagePill(plan, seconds, reserve))
                    return;
            }
            if (HandleQuestRage(plan)) return;
            if (RageModeManager.instance?.currentState == RageModeManager.RageModeStates.NotActive) return;
        }

        if (IsKillQuest(plan.Type) && !(plan.Type == QuestType.WindDashKills && !WindDashAutomationAvailable()))
        {
            // Giant spawn frequency is the primary bottleneck, including a specified Giant; do not check only KillGiants.
            if (plan.TargetsGiants && NeedsCoverage(SmartItem.ShardsNecklace, seconds) &&
                TryCraft(SmartItem.ShardsNecklace, Plugin.Config.ShardsNecklaceReserveUses.Value,
                    $"Quest {plan.Name}: increase target Giant spawn rate")) return;
            // Collaboration event quests ignore direct quest multipliers. Necklace may improve Giant spawn rate, but staffs are not wasted.
            if (!plan.IsCollab && !naturalOnlyDailyRage)
            {
                // Staffs overlap, but apply lower-cost marginal gains first and add tiers from remaining real kills.
                // Deathwave Scepter no longer consumes scarce Simurgh Feathers merely because its multiplier is largest.
                foreach (var (kind, bonus) in CraftableEffectModel.KillStaffsAscending())
                    if (_materials.ShouldCraftRecommendedKillStaff(plan, kind) &&
                        TryCraftUsefulKillStaff(plan, kind, bonus, seconds, reserve)) return;
            }
        }

        if (!plan.IsCollab && IsCollectableQuest(plan.Type) && QuestDirector.CanProgressCollectableQuest(plan.Type) &&
            NeedsCoverage(SmartItem.NecklaceOfCollectables, seconds) &&
            TryCraft(SmartItem.NecklaceOfCollectables, reserve, reason)) return;

        if (IsRandomBoxQuest(plan.Type))
            if (TryApplyRandomBoxQuestBoosters(plan)) return;

        if (plan.Type is QuestType.BonusStage or QuestType.BonusStageSections)
        {
            if (NeedsActivation(SmartItem.Specialization) && TryCraft(SmartItem.Specialization, reserve, reason)) return;
            if (NeedsCoverage(SmartItem.RandomStone, seconds) && TryCraft(SmartItem.RandomStone, reserve, reason)) return;
        }

        if (plan.Type == QuestType.ChestHuntChests && NeedsActivation(SmartItem.KeyManifest) &&
            TryCraft(SmartItem.KeyManifest, reserve, reason)) return;

        if (plan.Type == QuestType.DoCriticals && NeedsCoverage(SmartItem.CriticalSword, seconds) &&
            TryCraft(SmartItem.CriticalSword, Plugin.Config.CriticalSwordReserveUses.Value, reason)) return;

        if (plan.Type == QuestType.GetMaterials && MapEfficiencyEstimator.ExpectedMaterialKills(
                Math.Max(0, plan.Goal - plan.Current), false) >=
            Math.Max(1, Plugin.Config.WhetstoneMinimumExpectedEligibleKills.Value) && Plugin.Config.WhetstoneDuringMaterialRecovery.Value &&
            NeedsCoverage(SmartItem.Whetstone, seconds) && TryCraft(SmartItem.Whetstone, Plugin.Config.WhetstoneReserveUses.Value, reason)) return;

        if (plan.Type == QuestType.CraftTemporaryItems)
        {
        // Keep the useful-item fallback only when regular Dragon Eggs are insufficient or below reserve. The normal path
        // opens exactly the remaining quest count before material scheduling.
            if (NeedsCoverage(SmartItem.RandomStone, seconds) && TryCraft(SmartItem.RandomStone, reserve, reason)) return;
            if (NeedsCoverage(SmartItem.Whetstone, seconds) && TryCraft(SmartItem.Whetstone, Plugin.Config.WhetstoneReserveUses.Value, reason)) return;
        }

        if (plan.Type == QuestType.UseRageMode || plan.RequiresRageForBowAutomation)
            HandleQuestRage(plan);
    }

    internal static bool CanCompleteCraftQuestWithDragonEggs(double remaining)
    {
        if (Plugin.Config?.CraftQuestUseDragonEggs.Value != true || remaining <= 0) return false;
        try
        {
            var egg = Drops.list?.DragonEgg;
            var opener = CraftableItems.list?.CrackDragonEgg;
            if (egg == null || opener == null || !opener.TabVisible()) return false;
            var reserve = Math.Max(0, Plugin.Config.CraftQuestDragonEggReserveAmount.Value);
            return egg.amount - reserve + 0.0001 >= Math.Ceiling(remaining);
        }
        catch { return false; }
    }

    private bool TryOpenDragonEggForCraftQuest(Quest quest)
    {
        if (quest == null || quest.questType != QuestType.CraftTemporaryItems ||
            quest.questCurrentGoal >= quest.questGoal ||
            !Plugin.Config.CraftQuestUseDragonEggs.Value) return false;
        var now = Time.unscaledTime;
        if (now < _nextCraftQuestEggAttemptAt) return false;
        _nextCraftQuestEggAttemptAt = now + 0.45f;

        var egg = Drops.list?.DragonEgg;
        var opener = CraftableItems.list?.CrackDragonEgg;
        var reserve = Math.Max(0, Plugin.Config.CraftQuestDragonEggReserveAmount.Value);
        var remainingCrafts = Math.Max(0, quest.questGoal - quest.questCurrentGoal);
        if (egg == null || opener == null || egg.amount <= reserve + 0.0001)
        {
            LogCraftQuestEggUnavailable(quest, egg == null || opener == null
                ? "Dragon Egg or egg-opening craftable interface is not ready"
                : $"Regular Dragon Eggs {egg.amount:0} reached reserve {reserve}");
            return false;
        }
        if (egg.amount - reserve + 0.0001 < Math.Ceiling((double)remainingCrafts))
        {
            LogCraftQuestEggUnavailable(quest,
                $"Only {Math.Max(0, egg.amount - reserve):0} regular Dragon Eggs remain above reserve, insufficient for {remainingCrafts:0} remaining crafts; wait for enough stock rather than consume and abandon");
            return false;
        }

        try
        {
            var lootManager = LootBoxManager.instance;
            if (lootManager != null && lootManager.IsBeingOpened()) return false;
            if (!opener.TabVisible() || !opener.ExtraCondition() || opener.HowManyCanCraft() <= 0)
            {
                LogCraftQuestEggUnavailable(quest, "Dragon Egg opening is not currently craftable");
                return false;
            }
            var lootBox = opener.lootBoxOpen;
            if (lootBox == null)
            {
                LogCraftQuestEggUnavailable(quest, "Dragon Egg reward interface is not ready");
                return false;
            }

            var before = egg.amount;
            var spent = false;
            try
            {
                opener.lootBoxOpen = null;
                opener.Craft();
                spent = egg.amount + 0.0001 < before;
                if (spent) lootBox.Open();
            }
            finally
            {
                opener.lootBoxOpen = lootBox;
            }
            if (!spent)
            {
                LogCraftQuestEggUnavailable(quest, "Native Craft did not consume an egg; stopping this cycle to avoid duplicate rewards");
                return false;
            }

            _lastCraftAt = now;
            InvalidateMaterialPlan();
            SmartLog.Info(SmartLogCategory.Quest,
                $"Opened one regular Dragon Egg for a temporary-item crafting quest; questInstanceId={quest.GetInstanceID()}, " +
                $"progress={quest.questCurrentGoal}/{quest.questGoal}, eggs={before:0}->{egg.amount:0}, reserve={reserve}.");
            return true;
        }
        catch (Exception ex)
        {
            LogCraftQuestEggUnavailable(quest, $"Background egg opening failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void LogCraftQuestEggUnavailable(Quest quest, string reason)
    {
        if (Time.unscaledTime < _nextCraftQuestEggLogAt) return;
        _nextCraftQuestEggLogAt = Time.unscaledTime + 30f;
        SmartLog.Debug(SmartLogCategory.Quest,
            $"Deferred Dragon Egg use for a temporary-item crafting quest; questInstanceId={quest.GetInstanceID()}.");
    }

    private bool TryApplyRandomBoxQuestBoosters(QuestPlan plan)
    {
        if (plan?.Quest == null || !IsRandomBoxQuest(plan.Type) || plan.Current >= plan.Goal) return false;
        var remaining = Math.Max(0, plan.Goal - plan.Current);
        var liveCredit = ActiveRandomBoxCredit(plan);
        var actionsBefore = Math.Ceiling(remaining / liveCredit);
        var targetSeconds = Math.Max(10f, Plugin.Config.QuestBoosterTargetSeconds.Value);
        var reserve = Math.Max(0, Plugin.Config.QuestBoosterReserveUses.Value);
            // HandleLiveRandomBoxStaff processes Random Box Staff after one live-box scan. Do not enumerate the Unity
            // scene again for every parallel box quest; Random Stone still raises only ordinary box frequency.
        return plan.Type is QuestType.HitRandomBoxes or QuestType.GetCoinsFromRandomBox && actionsBefore >= 2 &&
               NeedsCoverage(SmartItem.RandomStone, targetSeconds) &&
               TryCraft(SmartItem.RandomStone, reserve,
                   $"Quest {plan.Name}: increase ordinary Random Box frequency; active in parallel on material routes");
    }

    private void HandleLiveRandomBoxStaff()
    {
        var now = Time.unscaledTime;
        if (PaidSoulsBurstOwnsControl || now < _nextRandomBoxStaffCheckAt || !Plugin.Config.Enabled.Value || _emergencyStopped ||
            !Plugin.Config.QuestAutomationEnabled.Value ||
            GameState.current is not (GameStates.RunnerMode or GameStates.RageMode)) return;
        _nextRandomBoxStaffCheckAt = now + 0.2f;
        if (IsEffectActive(SmartItem.RandomBoxStaff) || !HasUsefulRandomBoxStaffPlan()) return;
        if (!TryFindLiveQuestRandomBox(out var targetDescription, out var plan)) return;
        var remaining = Math.Max(0, plan.Goal - plan.Current);
        var liveCredit = ActiveRandomBoxCredit(plan);
        var actionsBefore = Math.Ceiling(remaining / liveCredit);
        var actionsAfter = Math.Ceiling(remaining / (liveCredit + 5));
        TryCraft(SmartItem.RandomBoxStaff, Math.Max(0, Plugin.Config.QuestBoosterReserveUses.Value),
            $"Quest {plan.Name}: spawned {targetDescription}, expected hit actions {actionsBefore:0} -> {actionsAfter:0}; activate immediately",
            ignoreGlobalInterval: true);
    }

    private bool HasUsefulRandomBoxStaffPlan()
    {
        if (UsefulRandomBoxStaffPlan(_questPlan)) return true;
        if (_parallelQuestPlans == null) return false;
        for (var i = 0; i < _parallelQuestPlans.Count; i++)
            if (UsefulRandomBoxStaffPlan(_parallelQuestPlans[i])) return true;
        return false;
    }

    private static bool UsefulRandomBoxStaffPlan(QuestPlan plan)
    {
        if (plan?.Quest == null || !RandomBoxStaffSupports(plan.Type) || plan.Current >= plan.Goal || plan.IsCollab)
            return false;
        var remaining = Math.Max(0, plan.Goal - plan.Current);
        var liveCredit = ActiveRandomBoxCredit(plan);
        return Math.Ceiling(remaining / liveCredit) - Math.Ceiling(remaining / (liveCredit + 5)) >= 1;
    }

    private QuestPlan FindBestLiveRandomBoxStaffPlan(bool silver, bool gold)
    {
        QuestPlan best = null;
        var bestRank = -1;
        Consider(_questPlan);
        if (_parallelQuestPlans != null)
            for (var i = 0; i < _parallelQuestPlans.Count; i++) Consider(_parallelQuestPlans[i]);
        return best;

        void Consider(QuestPlan candidate)
        {
            if (!UsefulRandomBoxStaffPlan(candidate)) return;
            var rank = candidate.Type switch
            {
                QuestType.HitRandomSilverBoxes when silver => 2,
                QuestType.HitRandomGoldenBoxes when gold => 2,
                QuestType.HitRandomBoxes => 1,
                _ => -1
            };
            if (rank > bestRank)
            {
                best = candidate;
                bestRank = rank;
            }
        }
    }

    private bool TryFindLiveQuestRandomBox(out string description, out QuestPlan plan)
    {
        foreach (var box in UnityEngine.Object.FindObjectsOfType<RandomBox>())
        {
            if (box == null || box.isHitted || box.gameObject == null || !box.gameObject.activeInHierarchy) continue;
            var silver = box.type == RandomBox.Type.Silver;
            var gold = box.type == RandomBox.Type.Gold;
            plan = FindBestLiveRandomBoxStaffPlan(silver, gold);
            if (plan == null) continue;
            description = $"{box.type} Random Box";
            return true;
        }

        foreach (var box in UnityEngine.Object.FindObjectsOfType<SpecialRandomBox>())
        {
            if (box == null || box.isHitted || box.gameObject == null || !box.gameObject.activeInHierarchy) continue;
            plan = FindBestLiveRandomBoxStaffPlan(silver: false, gold: false);
            if (plan == null) continue;
            description = "Special Random Box";
            return true;
        }

        description = string.Empty;
        plan = null;
        return false;
    }

    private bool HandleQuestRage(QuestPlan plan)
    {
        var rage = RageModeManager.instance;
        if (rage == null) return false;
        var cap = Math.Max(0, Plugin.Config.MaximumRagePillsPerQuestSession.Value);

        if (rage.currentState == RageModeManager.RageModeStates.NotActive)
        {
            if (rage.currentCd <= 0.01)
            {
                rage.Activate();
                SmartLog.Info(SmartLogCategory.Quest,
                    $"Activated Rage for a quest window; questInstanceId={plan.InstanceId}.");
                return true;
            }
            if (plan.Source == QuestSource.Daily && plan.Type == QuestType.KillEnemiesWithRageMode)
                return false; // Dailies use natural cooldown only and never spend pills or start dedicated supply.
            if (Plugin.Config.QuestRagePillEnabled.Value && _ragePillsThisQuest < cap &&
                TryCraft(SmartItem.RagePill, Plugin.Config.RagePillReserveUses.Value,
                    $"Shared Rage quest group {plan.Name}"))
            {
                _ragePillsThisQuest++;
                return true;
            }
            return false;
        }

        // Current Rage may already complete the quest. Wait for NotActive and confirm a remaining gap before clearing cooldown.
        return false;
    }

    private bool DedicatedQuestSupplyInProgress(QuestPlan plan)
    {
        if (plan?.Quest == null || _materialNeed?.Material == null || _materialNeed.Opportunistic ||
            _materialNeed.QuestPlanId != plan.InstanceId) return false;
        try { return _materialNeed.Material.amount + 0.0001 < _materialNeed.Desired; }
        catch { return false; }
    }

    private bool StageRecommendedStaffBeforeRagePill(QuestPlan plan, double seconds, int reserve)
    {
        if (!_materials.HasRecommendedKillStaff(plan)) return false;
        foreach (var (kind, bonus) in CraftableEffectModel.KillStaffsAscending())
        {
            if (!_materials.ShouldCraftRecommendedKillStaff(plan, kind) || EffectSeconds(kind) > 1) continue;
            // Block the pill regardless of craft success. Failure usually means combined supply is incomplete and the next
            // deep material plan will continue; never degrade into spending a pill without staffs.
            TryCraftUsefulKillStaff(plan, kind, bonus, seconds, reserve);
            return true;
        }
        return false;
    }

    private bool HandleRequiredCharacterTask()
    {
        var now = Time.unscaledTime;
        if (now < _nextCharacterSwitchCheckAt || GameState.current != GameStates.RunnerMode) return false;
        _nextCharacterSwitchCheckAt = now + 1f;

        var candidates = new List<QuestPlan>();
        if (_questPlan?.Quest != null && _questPlan.Type == QuestType.KillEnemiesWithCharacter &&
            _questPlan.Current < _questPlan.Goal)
            candidates.Add(_questPlan);
        if (_parallelQuestPlans != null)
            candidates.AddRange(_parallelQuestPlans.Where(plan => plan?.Quest != null &&
                plan.Type == QuestType.KillEnemiesWithCharacter && plan.Current < plan.Goal &&
                candidates.All(existing => existing.InstanceId != plan.InstanceId)));
        if (candidates.Count == 0)
        {
            _lastCharacterSwitchQuestId = 0;
            return false;
        }

        var plan = candidates
            .OrderByDescending(candidate => candidate.InstanceId == _questPlan?.InstanceId)
            .ThenByDescending(candidate => candidate.StrategicUnlockBonus)
            .ThenByDescending(candidate => candidate.Source == QuestSource.Normal ? 3 :
                candidate.Source == QuestSource.Daily ? 2 : 1)
            .ThenByDescending(candidate => candidate.Score)
            .First();
        try
        {
            var required = plan.Quest.characterRequired;
            var manager = PlayerSkinManager.instance;
            if (required == null || manager == null) return false;
            if (!required.unlocked && !required.unlockedByDefault)
            {
                if (_lastCharacterSwitchQuestId != plan.InstanceId || now >= _nextCharacterSwitchErrorLogAt)
                {
                    _lastCharacterSwitchQuestId = plan.InstanceId;
                    _nextCharacterSwitchErrorLogAt = now + 30f;
                    SmartLog.Warning(SmartLogCategory.Quest,
                        $"Character-specific quest deferred because the required character is locked; requiredCharacter={required}.");
                }
                return false;
            }
            if (manager.CheckRequiredSkin(required)) return false;

            manager.ApplySkin(required);
            _lastCharacterSwitchQuestId = plan.InstanceId;
            SmartLog.Info(SmartLogCategory.Quest,
                $"Switched character for a character-specific quest; requiredCharacter={required}. The current composite route is unchanged.");
            return true;
        }
        catch (Exception ex)
        {
            if (now >= _nextCharacterSwitchErrorLogAt)
            {
                _nextCharacterSwitchErrorLogAt = now + 30f;
                SmartLog.Warning(SmartLogCategory.Quest,
                    $"Failed to switch character for a quest; retrying later. {ex.GetType().Name}: {ex.Message}");
            }
            return false;
        }
    }

    private static string CharacterName(CharacterSkin skin) =>
        string.IsNullOrWhiteSpace(skin?.localizedName) ? skin?.name ?? "Unknown Character" : skin.localizedName;

    private bool TryCraftUsefulKillStaff(QuestPlan plan, SmartItem kind, int bonus, double targetSeconds,
        int reserveUses)
    {
        if (!NeedsCoverage(kind, targetSeconds)) return false;
        var remaining = Math.Max(0, plan.Goal - plan.Current);
        if (remaining <= 0) return false;
        var currentCredit = ActiveKillCreditPerEnemy();
        var actionsBefore = Math.Ceiling(remaining / currentCredit);
        if (IsEffectActive(kind))
        {
            if (plan.ExpectedSeconds <= EffectSeconds(kind) + 5 ||
                actionsBefore < StrategyMath.KillStaffMinimumActionsSaved(kind)) return false;
            return TryCraft(kind, reserveUses,
                $"Quest {plan.Name}: extend coverage, expected remaining {plan.ExpectedSeconds:0}s, current {EffectSeconds(kind):0}s");
        }
        var actionsAfter = Math.Ceiling(remaining / (currentCredit + bonus));
        if (!StrategyMath.KillStaffSavesEnough(remaining, currentCredit, kind, bonus)) return false;
        return TryCraft(kind, reserveUses,
            $"Quest {plan.Name}: expected real kills {actionsBefore:0} -> {actionsAfter:0}");
    }

    private double ActiveKillCreditPerEnemy()
    {
        try { return Math.Max(1, PlayerInventory.instance?.questProgressPerKill ?? 1); }
        catch { return 1; }
    }

    private static double ActiveRandomBoxCredit(QuestPlan plan)
    {
        if (plan?.IsCollab == true) return 1;
        try { return Math.Max(1, PlayerInventory.instance?.randomBoxQuestsProgress ?? 1); }
        catch { return 1; }
    }

    private bool IsEffectActive(SmartItem kind)
    {
        var item = _catalog.Get(kind);
        if (item == null) return false;
        try { return item.IsActive() || item.currentTime > 0.05; }
        catch { return false; }
    }

    internal static bool SmartItemActive(SmartItem kind) => Current?.IsEffectActive(kind) == true;

    private double EffectSeconds(SmartItem kind)
    {
        var item = _catalog.Get(kind);
        if (item == null) return 0;
        try
        {
            if (PermanentClientBonuses.IsManagedTimedCraftable(item))
                return PermanentClientBonuses.PlanningCoverageSeconds;
            return Math.Max(0, item.currentTime);
        }
        catch { return 0; }
    }

    private bool TryOpenQuestPortal(BaseMap target, string reason, float? retrySeconds = null,
        string logType = "Quest")
    {
        if (target == null || SameMap(target, MapController.instance?.selectedMap))
        {
            _portalDeferralKey = null;
            return false;
        }
        if (!Plugin.EndgameModeActive && ReadHordeActive())
        {
            LogPortalDeferral(target, "Horde event is active; clear locally and use Rage first", logType);
            return false;
        }
        if (!Plugin.EndgameModeActive && IsQualifyingSoulEvent(ReadSoulsEvent()) &&
            !_permanentFreeSoulsEventOwned)
        {
            LogPortalDeferral(target, "A high-multiplier Souls event is active; finish the current burst window first", logType);
            return false;
        }
        var rage = RageModeManager.instance;
        if (!Plugin.EndgameModeActive && rage != null &&
            rage.currentState != RageModeManager.RageModeStates.NotActive)
        {
            LogPortalDeferral(target, "Rage is still active; preserve the current kill window", logType);
            return false;
        }
        var retry = retrySeconds.HasValue
            ? Math.Max(1f, retrySeconds.Value)
            : Math.Max(10f, Plugin.Config.PortalRetrySeconds.Value);
        if (Time.unscaledTime - _portalRequestedAt < retry) return false;
        if (!CanOpenPortal(out var portal)) return false;
        _portalGroundReservationUntil = Time.unscaledTime + RuntimeCadence.PortalGroundReservationSeconds;
        if (!PlayerGroundedForPortal())
        {
            LogPortalDeferral(target, "Wait for landing and automatic jump completion to avoid passing above the Portal", logType);
            return false;
        }
        try
        {
            if (!target.IsAvailable())
            {
                _portalGroundReservationUntil = 0;
                return false;
            }
            portal.SpawnPortal(target, false);
            _portalRequestedAt = Time.unscaledTime;
            _portalGroundReservationUntil = 0;
            _portalDeferralKey = null;
            SmartLog.Info(SmartLogCategory.Portal,
                $"Opened a normal Portal; targetMap={target.name}.");
            return true;
        }
        catch (Exception ex)
        {
            _portalRequestedAt = Time.unscaledTime;
            _portalGroundReservationUntil = 0;
            SmartLog.Warning(SmartLogCategory.Portal,
                $"Failed to open a Portal; waiting before retry. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void LogPortalDeferral(BaseMap target, string cause, string logType = "Quest")
    {
        var current = MapController.instance?.selectedMap?.name ?? "unknown";
        var targetName = target?.name ?? "unknown";
        var key = $"{logType}|{current}|{targetName}|{cause}";
        if (_portalDeferralKey == key) return;
        _portalDeferralKey = key;
        SmartLog.Debug(SmartLogCategory.Portal,
            $"Portal travel deferred; currentMap={current}, targetMap={targetName}. The latest plan will be used when the guard clears.");
    }

    private static BaseMap FindAlternativeQuestMap()
    {
        var controller = MapController.instance;
        if (controller?.maps == null) return null;
        var current = controller.selectedMap;
        for (var i = 0; i < controller.maps.Length; i++)
        {
            var map = controller.maps[i];
            if (map == null || SameMap(map, current)) continue;
            try { if (QuestDirector.IsStandardAvailableMap(map)) return map; }
            catch { /* Skip story maps or uninitialized maps. */ }
        }
        return null;
    }

    private static bool SameMap(BaseMap left, BaseMap right)
    {
        if (left == null || right == null) return false;
        var leftId = left.id ?? left.name;
        var rightId = right.id ?? right.name;
        return left == right || (!string.IsNullOrEmpty(leftId) && leftId == rightId);
    }

    private bool NeedsActivation(SmartItem kind)
    {
        var item = _catalog.Get(kind);
        if (item == null) return false;
        try { return !item.IsActive(); }
        catch { return NeedsCoverage(kind, 1); }
    }

    private static bool IsKillQuest(QuestType type) => type is
        QuestType.KillEnemies or QuestType.KillEnemiesWithArrows or QuestType.KillFlyers or QuestType.KillGiants or
        QuestType.KillEnemiesWithRageMode or QuestType.KillEnemiesWithCharacter or QuestType.KillEnemiesOfType or
        QuestType.KillShinyEnemies or QuestType.WindDashKills;

    private static bool IsCollectableQuest(QuestType type) => type is
        QuestType.PickUpCoins or QuestType.PickUpRubies or QuestType.PickUpSapphires or QuestType.PickUpEmeralds or
        QuestType.PickUpGemstones or QuestType.PickUpDiamonds or QuestType.PickUpZyniums;

    private static bool IsRandomBoxQuest(QuestType type) => type is
        QuestType.HitRandomBoxes or QuestType.HitRandomSilverBoxes or QuestType.HitRandomGoldenBoxes or QuestType.GetCoinsFromRandomBox;

    private static bool RandomBoxStaffSupports(QuestType type) => type is
        QuestType.HitRandomBoxes or QuestType.HitRandomSilverBoxes or QuestType.HitRandomGoldenBoxes;

    private bool TryCraft(SmartItem kind, int reserveUses, string reason, bool ignoreGlobalInterval = false,
        bool allowReserveBorrow = false, bool ignoreMaterialReserve = false)
    {
        var now = Time.unscaledTime;
        if (!ignoreGlobalInterval && now - _lastCraftAt < 0.45f) return false;
        if (_craftBackoffUntil.TryGetValue(kind, out var blockedUntil) && now < blockedUntil) return false;
        var item = _catalog.Get(kind);
        if (item == null) return false;

        if (IsKillQuestStaff(kind) && PortalQuestProtectionActive())
        {
            LogPortalQuestProtection($"Pause crafting {DisplayName(item, kind)}");
            return false;
        }

        try
        {
            if (!item.TabVisible())
            {
                LogCraftSkip(kind, item, "Locked or crafting tab is hidden");
                return false;
            }
            if (!item.ExtraCondition())
            {
                LogCraftSkip(kind, item, "Additional native crafting condition is not met");
                return false;
            }
            if (!ignoreMaterialReserve &&
                !MaterialReservePolicy.CanCraftOnce(item, out var materialReserveReason))
            {
                LogCraftSkip(kind, item, materialReserveReason);
                return false;
            }
            var craftable = item.HowManyCanCraft();
            var reserve = Math.Max(0, reserveUses);
            if (double.IsNaN(craftable) || double.IsInfinity(craftable))
            {
                LogCraftSkip(kind, item, "Native craftable-use count is temporarily invalid");
                return false;
            }
            if (allowReserveBorrow && craftable >= 1 && craftable <= reserve)
            {
                reserve = Math.Max(0, Math.Min(reserve, (int)Math.Floor(craftable) - 1));
                SmartLog.Info(SmartLogCategory.Material,
                    $"Temporarily borrowed crafting reserve for a high-value window; craftable={kind}, availableCrafts={craftable:0}, reserve={reserve}.");
            }
            if (craftable <= reserve)
            {
                // Whetstone stockout immediately preempts every other material target; quest items also wake on-demand evaluation.
                if (kind == SmartItem.Whetstone || _questPlan != null) InvalidateMaterialPlan();
                LogCraftSkip(kind, item,
                    kind == SmartItem.Whetstone
                        ? $"Currently craftable {craftable:0} times; highest-priority Whetstone material supply started immediately"
                        : $"Currently craftable {craftable:0} times with reserve {reserve} required after crafting; deferred to the on-demand supply value model");
                return false;
            }
            var before = item.currentTime;
            item.Craft();
            _lastCraftAt = now;
            InvalidateMaterialPlan();
            _craftBackoffUntil[kind] = now + RuntimeCadence.CraftActionBackoffSeconds;
            SmartLog.Info(SmartLogCategory.Material,
                $"Crafted temporary item; craftable={kind}, availableCraftsBefore={craftable:0}, reserve={reserve}, " +
                $"duration={before:0.0}s->{item.currentTime:0.0}s.");
            return true;
        }
        catch (Exception ex)
        {
            _craftBackoffUntil[kind] = now + RuntimeCadence.CraftFailureBackoffSeconds;
            SmartLog.Warning(SmartLogCategory.Material,
                $"Failed to craft {kind}; retry is suppressed for 30s. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void LogCraftSkip(SmartItem kind, TemporaryCraftableItem item, string reason)
    {
        var now = Time.unscaledTime;
        if (_nextCraftSkipLogAt.TryGetValue(kind, out var next) && now < next) return;
        _nextCraftSkipLogAt[kind] = now + RuntimeCadence.CraftSkipLogSeconds;
        SmartLog.Debug(SmartLogCategory.Material,
            $"Crafting deferred; craftable={kind}.");
    }

    private static bool IsWeeklyPortalPlan(QuestPlan plan) =>
        plan?.Source == QuestSource.Weekly && plan.Type == QuestType.GoThroughPortals;

    private static bool IsKillQuestStaff(SmartItem kind) => kind is
        SmartItem.DeathwaveScepter or SmartItem.BidimensionalStaff or SmartItem.AlternateDimensionStaff;

    private bool PortalQuestProtectionActive() => Time.unscaledTime < _portalQuestProtectionUntil;

    private void ExtendPortalQuestProtection(string reason)
    {
        var now = Time.unscaledTime;
        var seconds = Math.Max(1f, Plugin.Config.WeeklyPortalProtectionSeconds.Value);
        var wasInactive = now >= _portalQuestProtectionUntil;
        _portalQuestProtectionUntil = Math.Max(_portalQuestProtectionUntil, now + seconds);

        if (_materialNeed != null && _materialNeed.QuestPlanId != 0)
        {
            SmartLog.Info(SmartLogCategory.Portal,
                "Cancelled a stale material target while a Portal quest protection window is active.");
            _materialNeed = null;
            ClearMaterialLinger();
            InvalidateMaterialPlan();
        }
        if (wasInactive) LogPortalQuestProtection($"{reason}; protect for {seconds:0.#}s");
    }

    private void LogPortalQuestProtection(string action)
    {
        var now = Time.unscaledTime;
        if (now < _nextPortalProtectionLogAt) return;
        _nextPortalProtectionLogAt = now + 10f;
        SmartLog.Info(SmartLogCategory.Portal,
            "Portal quest protection is active; quest-staff crafting and supply are paused while baseline safety stock remains enabled.");
    }

    private bool NeedsCoverage(SmartItem kind, double targetSeconds)
    {
        var item = _catalog.Get(kind);
        if (item == null) return false;
        try
        {
            if (PermanentClientBonuses.IsManagedTimedCraftable(item)) return false;
            return item.currentTime + 0.05 < Math.Max(0, targetSeconds);
        }
        catch { return false; }
    }

    private bool IsRunnerStable()
    {
        var state = GameState.current;
        var map = MapController.instance;
        var valid = (state == GameStates.RunnerMode || state == GameStates.RageMode) &&
                    map != null && map.initialized && !map.changingMap && map.selectedMap != null;
        if (!valid)
        {
            _runnerValidSince = -1;
            return false;
        }
        if (_runnerValidSince < 0)
        {
            _runnerValidSince = Time.unscaledTime;
            return false;
        }
        return Time.unscaledTime - _runnerValidSince >= Math.Max(0, Plugin.Config.StableRunnerSeconds.Value);
    }

    private static bool CanExecuteSoulsBurst(bool stable)
    {
        if (!stable) return false;
        return QuestDirector.IsStandardAvailableMap(MapController.instance?.selectedMap);
    }

    private void TrackSoulEvent(SoulsEventState state)
    {
        if (state.Active && !_soulEventWasActive)
        {
            _soulBurstDeferred = false;
            _soulDeferredLogWritten = false;
            _soulRecoveryLogWritten = false;
            _ragePillsThisEvent = 0;
            SmartLog.Info(SmartLogCategory.Event,
                $"Souls bonus event started; multiplier={state.Multiplier:0.#}x, remaining={state.SecondsLeft:0.0}s.");
        }
        else if (!state.Active && _soulEventWasActive)
        {
            SmartLog.Info(SmartLogCategory.Event,
                $"Souls bonus event ended; ragePillsUsed={_ragePillsThisEvent}.");
            _soulBurstDeferred = false;
            _soulDeferredLogWritten = false;
            _soulRecoveryLogWritten = false;
            _ragePillsThisEvent = 0;
            if (!PaidSoulsBurstOwnsControl)
                RequestImmediateRoutingReplan("Souls event ended; resume the latest composite quest route immediately");
        }
        _soulEventWasActive = state.Active;
    }

    private void TrackRageCycle()
    {
        var rage = RageModeManager.instance;
        var state = rage?.currentState ?? RageModeManager.RageModeStates.NotActive;
        var now = Time.unscaledTime;
        if (state != RageModeManager.RageModeStates.NotActive)
        {
            if (_previousRageState == RageModeManager.RageModeStates.NotActive || _lastRageWindowClockAt < 0)
            {
                try { _rageWindowDuration = Math.Max(1, rage?.GetDuration() ?? 20); }
                catch { _rageWindowDuration = 20; }
                _rageWindowElapsed = 0;
                _lastRageWindowClockAt = now;
            }
            else
            {
                if (RageTimerAdvancesInCurrentState())
                    _rageWindowElapsed += Math.Max(0, now - _lastRageWindowClockAt);
                _lastRageWindowClockAt = now;
            }
        }
        else
        {
            _rageWindowDuration = 0;
            _rageWindowElapsed = 0;
            _lastRageWindowClockAt = -1f;
        }
        if (_previousRageState == RageModeManager.RageModeStates.NotActive && state != RageModeManager.RageModeStates.NotActive)
            _pillAttemptedThisRage = false;
        if (state == RageModeManager.RageModeStates.NotActive) _pillAttemptedThisRage = false;
        _previousRageState = state;
    }

    internal double EstimatedRageSecondsRemaining
    {
        get
        {
            var remaining = Math.Max(0, _rageWindowDuration - _rageWindowElapsed);
            if (_lastRageWindowClockAt >= 0 && RageTimerAdvancesInCurrentState())
                remaining = Math.Max(0, remaining - Math.Max(0, Time.unscaledTime - _lastRageWindowClockAt));
            return remaining;
        }
    }

    internal int RemainingQuestRagePillAllowance(int questPlanId)
    {
        var cap = Math.Max(0, Plugin.Config.MaximumRagePillsPerQuestSession.Value);
        // Concurrent Rage kills, Rage uses, and forced Rage-kill targets share one activation and one pill budget. The
        // material model's current representative cannot count the same window as several pill allocations.
        return Math.Max(0, cap - _ragePillsThisQuest);
    }

    private static bool RageTimerAdvancesInCurrentState() => GameState.current is
        GameStates.RunnerMode or GameStates.RageMode or GameStates.BattleMode;

    private void TrackMapChange()
    {
        var map = MapController.instance?.selectedMap;
        var id = map?.id ?? map?.name;
        if (string.IsNullOrEmpty(id)) return;
        if (_lastMapId == null)
        {
            _lastMapId = id;
            return;
        }
        if (_lastMapId == id) return;
        _lastMapId = id;
        _lastDimensionChangeAt = Time.unscaledTime;
        _portalRequestedAt = -1000f;
        _portalGroundReservationUntil = 0;
        _portalDeferralKey = null;
        InvalidateMaterialPlan();
        _nextIdleSoulMapScan = 0;
        SmartLog.Info(SmartLogCategory.Runtime,
            $"Map transition stabilized; map={map.name}.");
    }

    private static SoulsEventState ReadSoulsEvent()
    {
        try
        {
            var manager = RandomEventManager.instance;
            var effect = manager?.soulsBonus;
            if (effect == null || !effect.IsActive()) return default;
            return new SoulsEventState(true, manager.soulsBonusEventMultiplier, effect.timeLeft);
        }
        catch { return default; }
    }

    private static bool ReadHordeActive()
    {
        try { return RandomEventManager.instance?.horde?.IsActive() == true; }
        catch { return false; }
    }

    private static bool IsQualifyingSoulEvent(SoulsEventState state) => state.Active &&
        state.Multiplier >= Math.Max(1, Plugin.Config.MinimumSoulsMultiplier.Value);

    private void TrackHordeEvent(bool active)
    {
        if (active && !_hordeWasActive)
        {
            SmartLog.Info(SmartLogCategory.Event, Plugin.EndgameModeActive
                ? "Horde event started; endgame mode keeps route changes enabled while opportunistically using the current horde."
                : "Horde event started; clearing locally and using Rage only when its natural cooldown is ready. Cross-map travel is deferred.");
        }
        else if (!active && _hordeWasActive)
        {
            if (!PaidSoulsBurstOwnsControl)
            {
                RequestImmediateRoutingReplan("Horde event ended; resume the latest composite quest route immediately");
                SmartLog.Info(SmartLogCategory.Event,
                    "Horde event ended; material and composite quest routes were recalculated immediately.");
            }
        }
        _hordeWasActive = active;
    }

    private static bool CurrentMapHasGiant()
    {
        var map = MapController.instance?.selectedMap;
        if (map == null) return false;
        try
        {
            if (map.currentStageEnemies != null)
                for (var i = 0; i < map.currentStageEnemies.Count; i++)
                {
                    var enemy = MapEfficiencyEstimator.CurrentStage(map.currentStageEnemies[i]);
                    if (MapEfficiencyEstimator.IsEnemyUnlocked(enemy) && enemy.isGiant) return true;
                }
            if (map.enemies != null)
                for (var i = 0; i < map.enemies.Length; i++)
                {
                    var enemy = MapEfficiencyEstimator.CurrentStage(map.enemies[i]);
                    if (MapEfficiencyEstimator.IsEnemyUnlocked(enemy) && enemy.isGiant) return true;
                }
        }
        catch { return false; }
        return false;
    }

    private static bool CanOpenPortal(out PortalButton portal)
    {
        portal = PortalButton.instance;
        if (portal == null || portal.currentCd > 0 || !portal.isActiveAndEnabled || portal.gameObject == null || !portal.gameObject.activeInHierarchy)
            return false;
        var button = portal.GetComponent<Button>();
        return button == null || (button.isActiveAndEnabled && button.gameObject.activeInHierarchy && button.interactable);
    }

    private bool PlayerGroundedForPortal()
    {
        if (_automaticJumpHeld) return false;
        try
        {
            var player = PlayerMovement.instance;
            return player != null && player.gameObject != null && player.gameObject.activeInHierarchy &&
                   player.IsGrounded();
        }
        catch { return false; }
    }

    private static KeyCode ParseKey(string value, KeyCode fallback) =>
        Enum.TryParse<KeyCode>(value, true, out var parsed) ? parsed : fallback;

    private static string DisplayName(TemporaryCraftableItem item, SmartItem fallback) =>
        string.IsNullOrWhiteSpace(item.localizedName) ? fallback.ToString() : item.localizedName;

    private static string DisplayName(Drop drop) =>
        string.IsNullOrWhiteSpace(drop?.localizedName) ? drop?.name ?? "Unknown Material" : drop.localizedName;

    private readonly struct SoulsEventState
    {
        internal readonly bool Active;
        internal readonly double Multiplier;
        internal readonly double SecondsLeft;

        internal SoulsEventState(bool active, double multiplier, double secondsLeft)
        {
            Active = active;
            Multiplier = multiplier;
            SecondsLeft = secondsLeft;
        }
    }
}

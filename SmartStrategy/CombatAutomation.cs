using Il2Cpp;
using UnityEngine;

namespace SmartStrategy;

internal enum SmartCombatMode
{
    None,
    Bow,
    WindDash
}

internal enum SmartMovementMode
{
    None,
    Boost,
    WindDash
}

public sealed partial class StrategyBehaviour
{
    private readonly List<EnemyGameObject> _combatEnemyCache = new(128);
    private readonly HashSet<int> _combatEnemyIds = new();
    private readonly List<CollectableGameObject> _jumpCollectableCache = new(192);
    private readonly HashSet<int> _jumpCollectableIds = new();
    private readonly HashSet<string> _loggedJumpProfiles = new(StringComparer.Ordinal);
    private float _nextCombatDecisionAt;
    private float _nextCombatCacheRefreshAt;
    private float _nextBowAttackAt;
    private float _combatModeChangedAt;
    private float _combatSuspendedUntil;
    private float _nextCombatErrorLogAt;
    private float _nextCombatModeLogAt;
    private float _nextCombatActionLogAt;
    private float _nextBoostActionLogAt;
    private float _nextJumpAttemptAt;
    private float _automaticJumpReleaseAt;
    private float _nextJumpCollectableCacheRefreshAt;
    private float _nextJumpBoxScanAt;
    private float _nextJumpPriorityPickupScanAt;
    private float _nextJumpPortalScanAt;
    private float _portalGroundReservationUntil;
    private float _nextSelectedAbilityCheckAt;
    private float _selectedAbilityCooldownReadySince = -1f;
    private float _nextCombatWorldBlockerScanAt;
    private bool _combatWorldBlocked;
    private string _combatWorldBlockerReason;
    private string _combatCacheMapId;
    private string _jumpCollectableCacheMapId;
    private string _stableSelectedAbilityName = string.Empty;
    private int _stableSelectedAbilityChecks;
    private bool _windDashQuestForcedSelection;
    private string _abilityBeforeForcedWindDash = string.Empty;
    private float _nextWindDashQuestSwitchAt;
    private SmartCombatMode _combatMode;
    private SmartMovementMode _movementMode;
    private int _lastCombatVisibleTargets;
    private int _lastCombatDashTargets;
    private int _lastCombatReservedTargets;
    private double _lastCombatBowScore;
    private double _lastCombatDashScore;
    private string _lastCombatReason = "Not evaluated";
    private string _lastMovementReason = "Not evaluated";
    private string _lastJumpReason = "Not evaluated";
    private bool _automaticJumpHeld;
    private PlayerMovement _automaticJumpPlayer;
    private bool _nativeRageMovementOwned;
    private bool _automaticJumpForcedStateOverridden;
    private bool _automaticJumpYieldsToPriorityGiant;
    private bool _jumpTrackingLogged;
    private bool _jumpBoxActive;
    private float _jumpBoxLead = float.PositiveInfinity;
    private string _jumpBoxKind = string.Empty;
    private int _jumpBoxTargetId;
    private int _lastLoggedJumpBoxTargetId;
    private bool _jumpPriorityPickupActive;
    private float _jumpPriorityPickupLead = float.PositiveInfinity;
    private float _jumpPriorityPickupWorldY;
    private string _jumpPriorityPickupKind = string.Empty;
    private int _jumpPriorityPickupTargetId;
    private int _lastLoggedJumpPriorityPickupTargetId;
    private bool _jumpPortalActive;
    private float _jumpPortalLead = float.PositiveInfinity;
    private bool _automaticJumpForceOverridden;
    private float _automaticJumpOriginalForce;
    private float _automaticJumpAppliedForce;
    private int _jumpMotionTargetId;
    private float _jumpMotionPreviousLead = float.NaN;
    private float _jumpMotionPreviousSampleAt = -1f;
    private float _jumpObservedClosingSpeed;
    private CollectableMagnet _jumpCollectableMagnet;
    private float _nextJumpMagnetResolveAt;
    private bool _automaticJumpTrajectoryPending;
    private float _automaticJumpTrajectoryStartedAt;
    private float _automaticJumpTrajectoryReleasedAt;
    private float _automaticJumpTrajectoryStartWorldY;
    private float _automaticJumpTrajectoryPreviousWorldY;
    private float _automaticJumpTrajectoryApexWorldY;
    private float _automaticJumpTrajectoryPlannedHold;
    private float _automaticJumpTrajectoryPredictedRise;
    private float _automaticJumpTrajectoryForceMultiplier;
    private int _automaticJumpTrajectoryLogCount;
    private float _nextPriorityGiantContextScanAt;
    private string _priorityGiantContextMapId = string.Empty;
    private string _priorityGiantContextReason = string.Empty;

    private void HandleSmartCombat()
    {
        var now = Time.unscaledTime;
        if (NativeRageOwnsMovement())
        {
            YieldMovementToNativeRage();
            return;
        }
        if (_nativeRageMovementOwned)
        {
            _nativeRageMovementOwned = false;
            _nextCombatDecisionAt = 0;
            _nextSelectedAbilityCheckAt = 0;
            _nextJumpAttemptAt = now + 0.1f;
            ResetSelectedAbilityAutomation();
            ResetJumpMotionObservation();
        }
        if (Plugin.Config.AutomaticCombatEnabled.Value && now >= _combatSuspendedUntil &&
            GameState.current == GameStates.BonusMode && MapController.instance?.changingMap != true &&
            now - _sceneReadyAt >= 1f)
        {
            TryPulseBonusStageBoxJump(now);
            SetCombatMode(SmartCombatMode.None, "Bonus Stage box-only jump assistance is active", log: false);
            SetMovementMode(SmartMovementMode.None, "Bonus Stage movement remains owned by the stage runner");
            return;
        }

        if (!Plugin.Config.AutomaticCombatEnabled.Value || now < _combatSuspendedUntil ||
            GameState.current != GameStates.RunnerMode ||
            MapController.instance?.changingMap == true || now - _sceneReadyAt < 1f)
        {
            ReleaseAutomaticJump();
            SetCombatMode(SmartCombatMode.None, "Runner state is not stable", log: false);
            SetMovementMode(SmartMovementMode.None, "Runner state is not stable");
            return;
        }

        var player = PlayerMovement.instance;
        if (player == null || player.gameObject == null || !player.gameObject.activeInHierarchy)
        {
            SetCombatMode(SmartCombatMode.None, "Player object is not ready", log: false);
            SetMovementMode(SmartMovementMode.None, "Player object is not ready");
            return;
        }

        var abilities = AbilitiesManager.instance;
        var windDash = abilities?.windDash;
        HandleAutomaticMovement(abilities, now);
        if (windDash != null && SafeWindDashActive(windDash))
        {
            SetCombatMode(SmartCombatMode.WindDash, "Wind Dash is active", log: false);
            SetMovementMode(SmartMovementMode.WindDash, "Wind Dash is active");
            return;
        }

        if (now < _nextCombatDecisionAt)
            return;
        _nextCombatDecisionAt = now + Math.Clamp(Plugin.Config.CombatDecisionIntervalSeconds.Value, 0.05f, 1f);

        RefreshCombatEnemyCache(now);
        var camera = player.cam ?? Camera.main;
        if (camera == null || _combatEnemyCache.Count == 0)
        {
            ClearCombatDecision("Enemy pool or camera is not ready");
            return;
        }

        var playerPosition = player.transform.position;
        var playerViewport = camera.WorldToViewportPoint(playerPosition);
        var windDashAvailable = WindDashAutomationAvailable();
        var hardWindDash = windDashAvailable && _quests?.HasActiveWindDashKillQuest == true;
        var configuredDashLead = Math.Clamp(Plugin.Config.WindDashActivationViewportLead.Value, 0.08f, 0.85f);
        var dashLead = hardWindDash ? Math.Max(configuredDashLead, 0.55f) : configuredDashLead;
        var dashReservationLead = Math.Min(1.20f, dashLead + 0.50f);
        var dashVerticalReach = ResolveWindDashVerticalReach();
        var bowModeFactor = ReadModeSoulFactor(PlayerInventory.instance?.soulsBonusBowList);
        var dashModeFactor = ReadModeSoulFactor(PlayerInventory.instance?.windDashSoulsBonus);
        var hardBow = !hardWindDash && _questPlan?.Type == QuestKind.KillEnemiesWithArrows;
        var bowScore = 0.0;
        var bowNearScore = 0.0;
        var dashScore = 0.0;
        var bowReservationScore = 0.0;
        var dashReservationScore = 0.0;
        var visibleTargets = 0;
        var dashTargets = 0;
        var reservedDashTargets = 0;
        var hasPriorityDashTarget = false;
        var hasPriorityReservedDashTarget = false;

        for (var i = 0; i < _combatEnemyCache.Count; i++)
        {
            var enemyObject = _combatEnemyCache[i];
            if (!TryReadCombatEnemy(enemyObject, camera, playerViewport, playerPosition, dashLead,
                    dashReservationLead,
                    dashVerticalReach, out var target)) continue;

            visibleTargets++;
            var questMatch = IsCurrentQuestTarget(target.Enemy);
            var materialMatch = IsCurrentMaterialTarget(target.Enemy);
            var utility = target.Utility;
            if (questMatch) utility += 240;
            if (materialMatch) utility += 40;
            if (target.IsShiny) utility += 8;

            if (target.ArrowCompatible)
            {
                var scored = utility * bowModeFactor;
                bowScore += scored;
                if (target.InDashWindow) bowNearScore += scored;
                if (target.InDashReservationWindow) bowReservationScore += scored;
            }

            if (!windDashAvailable || !target.DashCompatible || !target.InDashReservationWindow) continue;
            var dashUtility = utility * dashModeFactor;
            dashReservationScore += dashUtility;
            reservedDashTargets++;
            if ((questMatch || materialMatch) && !target.ArrowCompatible)
                hasPriorityReservedDashTarget = true;
            if (target.InDashWindow)
            {
                dashScore += dashUtility;
                dashTargets++;
                if ((questMatch || materialMatch) && !target.ArrowCompatible)
                    hasPriorityDashTarget = true;
            }
        }

        if (dashTargets > 1)
        {
            // Wind Dash processes a whole group in one collision window, while the bow requires repeated native shots.
            // Cap the batch advantage so dense patterns cannot inflate one cooldown without bound.
            var batchFactor = 1 + Math.Min(2.0, (dashTargets - 1) * 0.35);
            dashScore *= batchFactor;
        }
        if (reservedDashTargets > 1)
        {
            var batchFactor = 1 + Math.Min(2.0, (reservedDashTargets - 1) * 0.35);
            dashReservationScore *= batchFactor;
        }

        _lastCombatVisibleTargets = visibleTargets;
        _lastCombatDashTargets = dashTargets;
        _lastCombatReservedTargets = reservedDashTargets;
        _lastCombatBowScore = bowScore;
        _lastCombatDashScore = dashScore;

        if (hardBow)
        {
            _lastCombatReason = "The current quest requires bow kills";
            SetCombatMode(SmartCombatMode.Bow, _lastCombatReason);
            TryShootBow(player, bowScore, now);
            return;
        }

        var dashReady = windDashAvailable && windDash != null && SafeWindDashReady(windDash);
        var dashCooldown = windDashAvailable && windDash != null ? SafeWindDashCooldown(windDash) : double.MaxValue;
        var minimumTargets = Math.Max(1, Plugin.Config.WindDashMinimumTargets.Value);
        var gain = 1 + Math.Max(0, Plugin.Config.WindDashMinimumGainPercent.Value) / 100.0;
        var holdSatisfied = _combatMode != SmartCombatMode.Bow ||
            now - _combatModeChangedAt >= Math.Max(0, Plugin.Config.CombatModeMinimumHoldSeconds.Value);
        var dashWins = (hardWindDash && dashTargets > 0) || hasPriorityDashTarget ||
            (dashTargets >= minimumTargets && dashScore > Math.Max(0.01, bowNearScore) * gain);
        var reservationWins = (hardWindDash && reservedDashTargets > 0) || hasPriorityReservedDashTarget ||
            (reservedDashTargets >= minimumTargets &&
             dashReservationScore > Math.Max(0.01, bowReservationScore) * gain);

        if (dashReady && dashWins && (hardWindDash || holdSatisfied))
        {
            _lastCombatReason = hardWindDash
                ? $"The active Wind Dash kill quest has {dashTargets} compatible target(s) in range"
                : hasPriorityDashTarget
                ? "A quest or material target ahead cannot be handled by the bow"
                : $"{dashTargets} dashable targets ahead; value score {dashScore:0.##} exceeds bow {bowNearScore:0.##}";
            if (TryActivateWindDash(windDash, now, _lastCombatReason)) return;
        }

        // Arrows otherwise kill the approaching group before it enters dashLead. Preserve the group only
        // when Wind Dash is already ready or will be ready very shortly; longer cooldowns continue using bow.
        if (windDashAvailable && reservationWins && reservedDashTargets > dashTargets &&
            (hardWindDash || dashCooldown <= 1.5) &&
            !MovementAbilityWorldBlockerPresent(now))
        {
            _lastCombatReason = $"Reserved bow pause for {reservedDashTargets} dash targets ahead; " +
                                $"{dashTargets} are in the activation zone, cooldown={dashCooldown:0.##}s";
            SetCombatMode(SmartCombatMode.WindDash, _lastCombatReason);
            SetMovementMode(SmartMovementMode.WindDash, _lastCombatReason);
            return;
        }

        if (hardWindDash)
        {
            _lastCombatReason = reservedDashTargets > 0
                ? $"The Wind Dash quest reserves {reservedDashTargets} target(s) while cooldown is {dashCooldown:0.##}s"
                : "The Wind Dash quest is active; bow fire is paused until a compatible dash target appears";
            SetCombatMode(SmartCombatMode.WindDash, _lastCombatReason);
            SetMovementMode(SmartMovementMode.WindDash, _lastCombatReason);
            return;
        }

        if (bowScore > 0)
        {
            _lastCombatReason = dashReady
                ? $"Bow score is higher or Wind Dash has insufficient targets ({dashTargets}/{minimumTargets})"
                : "Wind Dash is cooling down or unavailable; continuing bow clear";
            SetCombatMode(SmartCombatMode.Bow, _lastCombatReason);
            TryShootBow(player, bowScore, now);
            return;
        }

        ClearCombatDecision(visibleTargets == 0 ? "No active enemy is ahead" : "Current enemies are outside usable attack paths");
    }

    private static bool NativeRageOwnsMovement()
    {
        if (GameState.current == GameStates.RageMode) return true;
        var rage = RageModeManager.instance;
        return rage != null && rage.currentState != RageModeManager.RageModeStates.NotActive;
    }

    internal void YieldMovementToNativeRage()
    {
        // TryJump writes velocity without checking GameState. Native CheckAction skips Rage and
        // ActivateRageMode switches the body to Kinematic, where an injected jump does not fall.
        var player = _automaticJumpPlayer;
        var ownedJump = player != null && player == PlayerMovement.instance &&
            (_automaticJumpHeld || _automaticJumpForcedStateOverridden ||
             _automaticJumpForceOverridden || _automaticJumpTrajectoryPending);
        ReleaseAutomaticJump();
        if (!_nativeRageMovementOwned && ownedJump)
        {
            // Clear only the unfinished jump we started, once, before native Rage takes over.
            // Do not alter the mount's transform, gravity, or subsequent native movement.
            try
            {
                player.isJumping = false;
                player.jumpTimeCounter = 0;
                var body = player.rigidBody;
                if (body != null)
                {
                    var velocity = body.velocity;
                    if (float.IsFinite(velocity.y) && velocity.y > 0)
                        body.velocity = new Vector2(velocity.x, 0);
                }
            }
            catch { /* A scene transition may destroy the player before the handoff. */ }
        }
        _automaticJumpTrajectoryPending = false;
        _automaticJumpPlayer = null;
        if (!_nativeRageMovementOwned)
            SmartLog.Debug(SmartLogCategory.Combat,
                "Yielded jump, Boost, Wind Dash and bow control to native Rage movement.");
        _nativeRageMovementOwned = true;
        _lastJumpReason = "Native Rage controls movement; automatic jumping is suspended";
        SetCombatMode(SmartCombatMode.None, _lastJumpReason, log: false);
        SetMovementMode(SmartMovementMode.None, _lastJumpReason);
    }

    private bool CanStartAutomaticJump(PlayerMovement player)
    {
        if (player == null || player.forceIdle || NativeRageOwnsMovement() ||
            GameState.current is not (GameStates.RunnerMode or GameStates.BonusMode)) return false;
        if (player.forceIsJumping && !_automaticJumpForcedStateOverridden) return false;
        return player.rigidBody != null && player.rigidBody.bodyType == RigidbodyType2D.Dynamic;
    }

    private void RefreshCombatEnemyCache(float now)
    {
        var map = MapController.instance?.selectedMap;
        var mapId = map?.id ?? map?.name ?? string.Empty;
        var mapChanged = !string.Equals(mapId, _combatCacheMapId, StringComparison.Ordinal);
        if (!mapChanged && _combatEnemyCache.Count > 0 && now < _nextCombatCacheRefreshAt) return;

        _combatCacheMapId = mapId;
        _combatEnemyCache.Clear();
        _combatEnemyIds.Clear();
        var pools = EnemyPooler.instance?.pools;
        if (pools != null)
        {
            for (var i = 0; i < pools.Count; i++)
            {
                var parent = pools[i]?.parentObject?.transform;
                if (parent == null) continue;
                for (var childIndex = 0; childIndex < parent.childCount; childIndex++)
                {
                    var child = parent.GetChild(childIndex);
                    var enemy = child?.GetComponent<EnemyGameObject>();
                    if (enemy == null) continue;
                    var id = enemy.GetInstanceID();
                    if (_combatEnemyIds.Add(id)) _combatEnemyCache.Add(enemy);
                }
            }
        }

        // Retry quickly while the enemy pool initializes, then refresh infrequently to cover evolution or map rebuilds
        // without periodic stalls.
        _nextCombatCacheRefreshAt = now + (_combatEnemyCache.Count == 0 ? 1f : mapChanged ? 2f : 30f);
        SmartLog.Debug(SmartLogCategory.Combat,
            $"Enemy cache refreshed: map={map?.name ?? "unknown"}, pooled objects={_combatEnemyCache.Count}, " +
            $"next refresh in {_nextCombatCacheRefreshAt - now:0.#}s.");
    }

    private static bool TryReadCombatEnemy(EnemyGameObject enemyObject, Camera camera, Vector3 playerViewport,
        Vector3 playerPosition, float dashLead, float dashReservationLead, float dashVerticalReach,
        out CombatTarget target)
    {
        target = default;
        try
        {
            if (enemyObject == null || enemyObject.gameObject == null ||
                !enemyObject.gameObject.activeInHierarchy || !enemyObject.isActiveAndEnabled) return false;
            var stats = enemyObject.enemyStats;
            var enemy = MapEfficiencyEstimator.CurrentStage(stats?.enemy);
            if (stats == null || stats.dead || enemy == null || enemy.isGiant) return false;

            var position = enemyObject.transform.position;
            var viewport = camera.WorldToViewportPoint(position);
            if (viewport.z <= 0) return false;
            var horizontal = viewport.x - playerViewport.x;
            if (horizontal < -0.08f || horizontal > 1.35f) return false;

            var reward = 0.0;
            try { reward = Math.Max(0, stats.GetSoulsReward()); }
            catch { reward = Math.Max(0, enemy.soulsReward); }
            if (!double.IsFinite(reward)) reward = double.MaxValue;
            var utility = 1 + Math.Log10(1 + Math.Min(reward, 1e300));
            var colliderHalfHeight = 0f;
            try { colliderHalfHeight = Math.Max(0, enemyObject.boxCollider?.size.y * 0.5f ?? 0); }
        catch { /* Use base dash range for an uninitialized collider. */ }
            var verticalDistance = Math.Abs(position.y - playerPosition.y);
            var dashCompatible = !enemy.isFlying && verticalDistance <= dashVerticalReach + colliderHalfHeight;
            target = new CombatTarget(enemy, utility, !enemy.cantBeKilledWithArrows,
                dashCompatible, horizontal <= dashLead, horizontal <= dashReservationLead, stats.isShiny);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static float ResolveWindDashVerticalReach()
    {
        try
        {
            var size = AttackDetector.instance?.sizeOnWindDash.y ?? 0;
            if (float.IsFinite(size) && size > 0) return Math.Max(0.75f, size * 0.5f);
        }
        catch { /* Use conservative range during component initialization races. */ }
        return 1.5f;
    }

    private bool IsCurrentMaterialTarget(Enemy enemy)
    {
        if (enemy?.drop == null || _materialNeed?.Material == null) return false;
        try
        {
            return CraftableCatalog.Normalize(enemy.drop.name) == CraftableCatalog.Normalize(_materialNeed.Material.name);
        }
        catch { return false; }
    }

    private bool IsCurrentQuestTarget(Enemy enemy)
    {
        var quest = _questPlan?.Quest;
        if (quest == null) return false;
        try { return MapEfficiencyEstimator.QuestEnemyMatches(enemy, quest); }
        catch { return false; }
    }

    private static double ReadModeSoulFactor(Il2CppSystem.Collections.Generic.List<double> bonuses)
    {
        if (bonuses == null) return 1;
        var total = 0.0;
        try
        {
            for (var i = 0; i < bonuses.Count; i++)
            {
                var value = bonuses[i];
                if (double.IsFinite(value) && value > 0) total = Math.Min(1e300, total + value);
            }
        }
        catch { return 1; }
        // Absolute reward units may change by version. Log scaling preserves reliable relative value without allowing
        // very large numbers to overwhelm quest weight.
        return 1 + Math.Log10(1 + total);
    }

    private void TryShootBow(PlayerMovement player, double score, float now)
    {
        if (score <= 0 || player == null || player.bowDisabled || now < _nextBowAttackAt) return;
        try
        {
            player.ShootArrow();
            _nextBowAttackAt = now + Math.Clamp(Plugin.Config.BowAttackRequestIntervalSeconds.Value, 0.02f, 2f);
        }
        catch
        {
            _nextBowAttackAt = now + 0.25f;
        }
    }

    private bool TryActivateWindDash(WindDash windDash, float now, string reason)
    {
        if (windDash == null || !SafeWindDashReady(windDash) || MovementAbilityWorldBlockerPresent(now)) return false;
        try
        {
            windDash.Activate();
            windDash.currentCd = Math.Max(0, windDash.GetCooldown());
            SetCombatMode(SmartCombatMode.WindDash, reason, log: true);
            SetMovementMode(SmartMovementMode.WindDash, reason);
            if (now >= _nextCombatActionLogAt)
            {
                _nextCombatActionLogAt = now + 30f;
                SmartLog.Debug(SmartLogCategory.Combat,
                    $"Activated Wind Dash: close targets={_lastCombatDashTargets}, bow score={_lastCombatBowScore:0.##}, " +
                    $"dash score={_lastCombatDashScore:0.##}, cooldown={windDash.currentCd:0.##}s.");
            }
            return true;
        }
        catch (Exception ex)
        {
            SuspendCombatAfterError(now, $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool MovementAbilityWorldBlockerPresent(float now)
    {
        if (now - _portalRequestedAt < 12f)
        {
            _lastCombatReason = "A Portal is open; protecting its approach path";
            return true;
        }
        if (_jumpPriorityPickupActive)
        {
            _combatWorldBlocked = true;
            _combatWorldBlockerReason = $"{_jumpPriorityPickupKind} is present; pausing dash for priority pickup";
            _lastCombatReason = _combatWorldBlockerReason;
            return true;
        }
        if (now < _nextCombatWorldBlockerScanAt)
        {
            if (_combatWorldBlocked) _lastCombatReason = _combatWorldBlockerReason;
            return _combatWorldBlocked;
        }

        _nextCombatWorldBlockerScanAt = now + 0.5f;
        _combatWorldBlocked = false;
        _combatWorldBlockerReason = null;
        try
        {
            if (GameContracts.IsChestHuntKeyPresent())
            {
                _combatWorldBlocked = true;
                _combatWorldBlockerReason = "Chest Hunt Key is present; pausing dash to avoid passing it";
            }
            else if (GameContracts.IsSpecialRandomBoxPresent())
            {
                _combatWorldBlocked = true;
                _combatWorldBlockerReason = "Special Random Box is present; pausing dash to avoid passing it";
            }
            else
            {
                var portal = UnityEngine.Object.FindObjectOfType<Portal>();
                if (portal != null && portal.gameObject != null && portal.gameObject.activeInHierarchy)
                {
                    _combatWorldBlocked = true;
                    _combatWorldBlockerReason = "An active Portal is present; pausing dash to protect entry";
                }
            }
        }
        catch { /* Let native ability validation take over while scene objects are destroyed. */ }
        if (_combatWorldBlocked) _lastCombatReason = _combatWorldBlockerReason;
        return _combatWorldBlocked;
    }

    private void HandleAutomaticMovement(AbilitiesManager abilities, float now)
    {
        TryPulseAutomaticJump(now);
        if (now < _nextSelectedAbilityCheckAt) return;
        _nextSelectedAbilityCheckAt = now + 0.1f;

        try
        {
            UpdateWindDashQuestAbilitySelection(abilities, now);
            var selected = abilities?.selectedAbility;
            var selectedName = GetSelectedAbilityName(selected);
            if (string.Equals(selectedName, "WindDash", StringComparison.Ordinal))
            {
                ObserveStableSelectedAbility(selectedName);
                _selectedAbilityCooldownReadySince = -1f;
                SetMovementMode(SmartMovementMode.WindDash, "Wind Dash is selected and triggered by enemy position and value");
                return;
            }
            if (!string.Equals(selectedName, "Boost", StringComparison.Ordinal))
            {
                ResetSelectedAbilityAutomation(selectedName);
                SetMovementMode(SmartMovementMode.None, $"Selected ability {selectedName} is neither regular Boost nor Wind Dash");
                return;
            }
            if (!Plugin.Config.AutomaticBoostEnabled.Value)
            {
                ResetSelectedAbilityAutomation(selectedName);
                SetMovementMode(SmartMovementMode.None, "Automatic regular Boost is disabled in configuration");
                return;
            }

            SetMovementMode(SmartMovementMode.Boost, "Regular Boost is selected; using stable cooldown activation");
            if (!ObserveStableSelectedAbility(selectedName)) return;
            if (!selected.Unlocked())
            {
                _selectedAbilityCooldownReadySince = -1f;
                return;
            }
            if (selected.GetCurrentCooldown() > 0.01f)
            {
                _selectedAbilityCooldownReadySince = -1f;
                return;
            }
            if (_selectedAbilityCooldownReadySince < 0)
            {
                _selectedAbilityCooldownReadySince = now;
                return;
            }
            var activationDelay = Math.Max(0, Plugin.Config.AutomaticBoostActivationDelaySeconds.Value);
            if (now - _selectedAbilityCooldownReadySince < activationDelay || MovementAbilityWorldBlockerPresent(now))
                return;

            selected.Activate();
            selected.currentCd = Math.Max(0, selected.GetCooldown());
            _selectedAbilityCooldownReadySince = -1f;
            if (now >= _nextBoostActionLogAt)
            {
                _nextBoostActionLogAt = now + 30f;
                SmartLog.Debug(SmartLogCategory.Combat,
                    $"Activated the selected regular Boost; cooldown={selected.currentCd:0.##}s.");
            }
        }
        catch (Exception ex)
        {
            SuspendCombatAfterError(now, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void UpdateWindDashQuestAbilitySelection(AbilitiesManager abilities, float now)
    {
        if (abilities == null) return;

        var windDashQuestActive = _quests?.HasActiveWindDashKillQuest == true &&
                                 SafeWindDashUnlocked(abilities.windDash);
        var selectedName = GetSelectedAbilityName(abilities.selectedAbility);
        if (windDashQuestActive)
        {
            if (string.Equals(selectedName, "WindDash", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(_abilityBeforeForcedWindDash))
                    _windDashQuestForcedSelection = true;
                return;
            }

            if (now < _nextWindDashQuestSwitchAt) return;
            _nextWindDashQuestSwitchAt = now + 0.25f;
            if (string.IsNullOrEmpty(_abilityBeforeForcedWindDash) &&
                selectedName is not ("None" or "Unknown"))
                _abilityBeforeForcedWindDash = selectedName;

            if (!TrySwitchToAbility(abilities, "WindDash")) return;
            _windDashQuestForcedSelection = true;
            ResetSelectedAbilityAutomation("WindDash");
            SmartLog.Debug(SmartLogCategory.Combat,
                $"Selected Wind Dash for the active kill quest; previous ability={_abilityBeforeForcedWindDash}.");
            return;
        }

        RestoreAbilityAfterWindDashQuest(abilities, now, selectedName);
    }

    private void RestoreAbilityAfterWindDashQuest(AbilitiesManager abilities, float now, string selectedName)
    {
        if (!_windDashQuestForcedSelection && string.IsNullOrEmpty(_abilityBeforeForcedWindDash)) return;
        if (!string.Equals(selectedName, "WindDash", StringComparison.Ordinal))
        {
            _windDashQuestForcedSelection = false;
            _abilityBeforeForcedWindDash = string.Empty;
            return;
        }
        if (now < _nextWindDashQuestSwitchAt) return;

        _nextWindDashQuestSwitchAt = now + 0.25f;
        var targetName = string.IsNullOrEmpty(_abilityBeforeForcedWindDash) ? "Boost" : _abilityBeforeForcedWindDash;
        if (!TrySwitchToAbility(abilities, targetName) &&
            !string.Equals(targetName, "Boost", StringComparison.Ordinal) &&
            !TrySwitchToAbility(abilities, "Boost")) return;

        SmartLog.Debug(SmartLogCategory.Combat,
            $"Restored the selected movement ability after the Wind Dash quest; target={targetName}.");
        _windDashQuestForcedSelection = false;
        _abilityBeforeForcedWindDash = string.Empty;
        ResetSelectedAbilityAutomation(GetSelectedAbilityName(abilities.selectedAbility));
    }

    private static bool TrySwitchToAbility(AbilitiesManager abilities, string targetName)
    {
        if (abilities == null || string.IsNullOrEmpty(targetName)) return false;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var before = GetSelectedAbilityName(abilities.selectedAbility);
            if (string.Equals(before, targetName, StringComparison.Ordinal)) return true;
            abilities.SwitchToNextAbility();
            var after = GetSelectedAbilityName(abilities.selectedAbility);
            if (string.Equals(after, targetName, StringComparison.Ordinal)) return true;
            // Some game versions apply the selection on the next frame. Avoid queueing a full cycle in that case.
            if (string.Equals(after, before, StringComparison.Ordinal)) return false;
        }
        return string.Equals(GetSelectedAbilityName(abilities.selectedAbility), targetName, StringComparison.Ordinal);
    }

    private bool ObserveStableSelectedAbility(string selectedName)
    {
        if (!string.Equals(selectedName, _stableSelectedAbilityName, StringComparison.Ordinal))
        {
            _stableSelectedAbilityName = selectedName;
            _stableSelectedAbilityChecks = 1;
            _selectedAbilityCooldownReadySince = -1f;
            return false;
        }
        if (_stableSelectedAbilityChecks < 3) _stableSelectedAbilityChecks++;
        return _stableSelectedAbilityChecks >= 3;
    }

    private void ResetSelectedAbilityAutomation(string observedName = "")
    {
        _stableSelectedAbilityName = observedName ?? string.Empty;
        _stableSelectedAbilityChecks = 0;
        _selectedAbilityCooldownReadySince = -1f;
    }

    private static string GetSelectedAbilityName(Ability ability)
    {
        if (ability == null) return "None";
        try { return ((UnityEngine.Object)ability).GetIl2CppType()?.Name ?? "Unknown"; }
        catch { return "Unknown"; }
    }

    private void TryPulseBonusStageBoxJump(float now)
    {
        var player = PlayerMovement.instance;
        var camera = player?.cam ?? Camera.main;
        ObserveAutomaticJumpTrajectory(now, player);

        if (_automaticJumpHeld)
        {
            if (now < _automaticJumpReleaseAt) return;
            ReleaseAutomaticJump();
        }
        if (!Plugin.Config.AutomaticJumpEnabled.Value || now < _nextJumpAttemptAt ||
            player == null || camera == null) return;

        try
        {
            if (!player.IsGrounded()) return;
        }
        catch
        {
            if (player.isJumping) return;
        }

        var viewport = camera.WorldToViewportPoint(player.transform.position);
        if (viewport.z <= 0 ||
            !TryResolveRandomBoxJump(now, camera, viewport, player.jumpTime, out var holdSeconds, out var reason))
            return;

        _lastJumpReason = reason;
        if (holdSeconds <= 0)
        {
            _nextJumpAttemptAt = now + 0.02f;
            return;
        }
        _nextJumpAttemptAt = now + Math.Clamp(Plugin.Config.AutomaticJumpRetrySeconds.Value, 0.04f, 1f);
        StartAutomaticJump(now, holdSeconds, reason, forceMultiplier: 1f);
    }

    private void TryPulseAutomaticJump(float now)
    {
        var player = PlayerMovement.instance;
        var camera = player?.cam ?? Camera.main;
        ObserveAutomaticJumpTrajectory(now, player);

        // A portal must outrank an already committed collectable jump.  The portal task can spawn
        // one after a jump has begun, so release immediately during the request window as well as
        // when the active Portal component is visible.
        if (PortalJumpProtectionPresent(now, camera, player, out var portalReason))
        {
            if (_automaticJumpHeld) ReleaseAutomaticJump();
            _lastJumpReason = portalReason;
            _nextJumpAttemptAt = now + 0.03f;
            return;
        }

        if (TryProtectRandomBoxApproach(now, camera, player, out var waitForLaunch,
                out var boxApproachReason))
        {
            if (_automaticJumpHeld) ReleaseAutomaticJump();
            _lastJumpReason = boxApproachReason;
            if (waitForLaunch)
            {
                _nextJumpAttemptAt = now + 0.02f;
                return;
            }
            // The protection phase has ended. Let the ordinary box branch jump in this same frame.
            _nextJumpAttemptAt = 0;
        }

        // The old implementation pressed and released in the same frame, which always produced
        // the minimum hop.  Keep the pointer held across frames and release it at a height selected
        // from the upcoming collectable pattern.
        if (_automaticJumpHeld)
        {
            if (_automaticJumpYieldsToPriorityGiant &&
                TryFindPriorityGiant(now, camera, player, out var committedGiantLead,
                    out var committedGiantReason))
            {
                ReleaseAutomaticJump();
                _lastJumpReason = $"Stopped sustained coin ascent: a Giant required by {committedGiantReason} entered " +
                                  $"{committedGiantLead:0.000} viewport width ahead; landing for engagement";
                _nextJumpAttemptAt = now + 0.03f;
                return;
            }
            // Box and collectable commitments outrank Giants.  Once either jump starts, let its
            // selected hold duration finish.  The sole exception is a coin jump while the current
            // material plan explicitly needs a Giant drop; rare boxes and minigame pickups remain
            // protected from that override.
            if (now >= _automaticJumpReleaseAt)
            {
                ReleaseAutomaticJump();
            }
            else
            {
                return;
            }
        }

        if (!Plugin.Config.AutomaticJumpEnabled.Value || now < _nextJumpAttemptAt || player == null || camera == null)
            return;

        try
        {
            if (!player.IsGrounded()) return;
        }
        catch { if (player.isJumping) return; }

        var viewport = camera.WorldToViewportPoint(player.transform.position);
        if (viewport.z <= 0) return;

        // Explicit priority: minigame entry pickups > active boxes > collectable patterns > Giants.
        if (TryResolvePriorityPickupJump(now, player, camera, viewport, out var pickupHold,
                out var pickupForceMultiplier, out var pickupPredictedRise, out var pickupReason))
        {
            _lastJumpReason = pickupReason;
            if (pickupHold <= 0)
            {
                _nextJumpAttemptAt = now + 0.02f;
                return;
            }
            _nextJumpAttemptAt = now + Math.Clamp(Plugin.Config.AutomaticJumpRetrySeconds.Value, 0.04f, 1f);
            StartAutomaticJump(now, pickupHold, pickupReason, pickupForceMultiplier, pickupPredictedRise);
            return;
        }

        if (TryResolveRandomBoxJump(now, camera, viewport, player.jumpTime, out var boxHold, out var boxReason))
        {
            _lastJumpReason = boxReason;
            if (boxHold <= 0)
            {
                _nextJumpAttemptAt = now + 0.02f;
                return;
            }
            _nextJumpAttemptAt = now + Math.Clamp(Plugin.Config.AutomaticJumpRetrySeconds.Value, 0.04f, 1f);
            StartAutomaticJump(now, boxHold, boxReason, forceMultiplier: 1f);
            return;
        }

        // Conditional priority: a Giant that drops the material currently being recovered outranks
        // coins, but never outranks portals, minigame pickups or boxes.  Ordinary Giants retain the
        // lowest priority and do not suppress collectable jumps.
        if (TryFindPriorityGiant(now, camera, player, out var priorityGiantLead,
                out var priorityGiantReason))
        {
            _lastJumpReason = $"A Giant required by {priorityGiantReason} is {priorityGiantLead:0.000} viewport width ahead; " +
                              "staying grounded to enter BattleMode and advance the Giant target";
            _nextJumpAttemptAt = now + 0.03f;
            return;
        }

        RefreshJumpCollectableCache(now);
        var jumpReady = TryResolveAdaptiveJump(player, camera, viewport, out var holdSeconds, out var forceMultiplier,
            out var predictedApexRise, out var reason, out var hasCollectableTarget);
        if (hasCollectableTarget)
        {
            if (!jumpReady)
            {
                _lastJumpReason = reason;
                // A visible group is approaching but has not reached its launch point.  Recheck much
                // faster than the normal rejected-input retry so Boost speed cannot carry it past us.
                _nextJumpAttemptAt = now + 0.02f;
                return;
            }
            _nextJumpAttemptAt = now + Math.Clamp(Plugin.Config.AutomaticJumpRetrySeconds.Value, 0.04f, 1f);
            StartAutomaticJump(now, holdSeconds, reason, forceMultiplier, predictedApexRise,
                yieldsToPriorityGiant: true);
            return;
        }

        RefreshCombatEnemyCache(now);
        if (TryFindApproachingGiant(camera, viewport, out var giantLead))
            _lastJumpReason = $"No box or coin target is active; a Giant is {giantLead:0.00} viewport width ahead, so final priority stays grounded";
        else
            _lastJumpReason = reason;
        _nextJumpAttemptAt = now + 0.05f;
    }

    private void StartAutomaticJump(float now, float holdSeconds, string reason, float forceMultiplier,
        float predictedApexRise = float.NaN, bool yieldsToPriorityGiant = false)
    {
        var jumpStarted = false;
        try
        {
            var player = PlayerMovement.instance;
            if (!CanStartAutomaticJump(player)) return;
            _automaticJumpPlayer = player;
            var maximumHold = Math.Clamp(Plugin.Config.AutomaticJumpMaximumHoldSeconds.Value, 0.25f, 0.3f);
            holdSeconds = Math.Clamp(holdSeconds, 0.025f, maximumHold);
            var maximumForceMultiplier = Math.Clamp(Plugin.Config.AutomaticJumpMaximumForceMultiplier.Value, 1f, 2.5f);
            forceMultiplier = Math.Clamp(forceMultiplier, 1f, maximumForceMultiplier);
            if (player != null && forceMultiplier > 1.005f)
            {
                _automaticJumpOriginalForce = player.jumpForce;
                _automaticJumpAppliedForce = player.jumpForce * forceMultiplier;
                player.jumpForce = _automaticJumpAppliedForce;
                _automaticJumpForceOverridden = true;
            }

            // Match the game's own JumpButton.PlayerJump path.  JumpPanel.OnPointerDown is not a
            // valid synthetic hold: JumpPanel.LateUpdate sees no physical mouse/touch, treats its
            // pointer counter as stale and clears jumpPressed in the same frame.  forceIsJumping is
            // the native programmatic-jump flag and survives that cleanup until PlayerStopJump.
            if (!player.forceIsJumping)
            {
                player.forceIsJumping = true;
                _automaticJumpForcedStateOverridden = true;
            }
            var startWorldY = player.transform.position.y;
            player.TryJump();
            jumpStarted = true;
            _automaticJumpHeld = true;
            _automaticJumpYieldsToPriorityGiant = yieldsToPriorityGiant;
            _automaticJumpReleaseAt = now + holdSeconds;
            _lastJumpReason = reason;
            _automaticJumpTrajectoryPending = true;
            _automaticJumpTrajectoryStartedAt = now;
            _automaticJumpTrajectoryReleasedAt = 0;
            _automaticJumpTrajectoryStartWorldY = startWorldY;
            _automaticJumpTrajectoryPreviousWorldY = startWorldY;
            _automaticJumpTrajectoryApexWorldY = startWorldY;
            _automaticJumpTrajectoryPlannedHold = holdSeconds;
            _automaticJumpTrajectoryPredictedRise = predictedApexRise;
            _automaticJumpTrajectoryForceMultiplier = forceMultiplier;
        }
        catch
        {
            _automaticJumpHeld = false;
            _automaticJumpYieldsToPriorityGiant = false;
            RestoreAutomaticJumpForcedState();
            RestoreAutomaticJumpForce();
            if (!jumpStarted) _automaticJumpTrajectoryPending = false;
        }
    }

    private void ReleaseAutomaticJump()
    {
        if (!_automaticJumpHeld && !_automaticJumpForcedStateOverridden && !_automaticJumpForceOverridden) return;
        try
        {
            RestoreAutomaticJumpForcedState();
        }
        catch { /* Scene transitions rebuild PlayerMovement. */ }
        finally
        {
            _automaticJumpHeld = false;
            _automaticJumpYieldsToPriorityGiant = false;
            _automaticJumpTrajectoryReleasedAt = Time.unscaledTime;
            _automaticJumpReleaseAt = 0;
            RestoreAutomaticJumpForce();
        }
    }

    private void RestoreAutomaticJumpForcedState()
    {
        if (!_automaticJumpForcedStateOverridden) return;
        try
        {
            var player = _automaticJumpPlayer;
            if (player != null && player.forceIsJumping) player.forceIsJumping = false;
        }
        catch { /* Scene transitions rebuild PlayerMovement. */ }
        finally
        {
            _automaticJumpForcedStateOverridden = false;
        }
    }

    private void ObserveAutomaticJumpTrajectory(float now, PlayerMovement player)
    {
        if (!_automaticJumpTrajectoryPending || player == null) return;
        try
        {
            var worldY = player.transform.position.y;
            _automaticJumpTrajectoryApexWorldY = Math.Max(_automaticJumpTrajectoryApexWorldY, worldY);
            var descending = worldY < _automaticJumpTrajectoryPreviousWorldY - 0.001f;
            _automaticJumpTrajectoryPreviousWorldY = worldY;
            if (_automaticJumpHeld || !descending && now - _automaticJumpTrajectoryStartedAt < 1.5f) return;

            if (_automaticJumpTrajectoryLogCount < 8)
            {
                _automaticJumpTrajectoryLogCount++;
                var actualHold = _automaticJumpTrajectoryReleasedAt > 0
                    ? _automaticJumpTrajectoryReleasedAt - _automaticJumpTrajectoryStartedAt
                    : 0;
                var actualRise = _automaticJumpTrajectoryApexWorldY - _automaticJumpTrajectoryStartWorldY;
                var prediction = float.IsFinite(_automaticJumpTrajectoryPredictedRise)
                    ? _automaticJumpTrajectoryPredictedRise.ToString("0.000")
                    : "box path";
                SmartLog.Debug(SmartLogCategory.Combat,
                    $"Jump trajectory: planned/actual hold={_automaticJumpTrajectoryPlannedHold:0.000}/{actualHold:0.000}s, " +
                    $"predicted/actual apex rise={prediction}/{actualRise:0.000}, force={_automaticJumpTrajectoryForceMultiplier:0.00}x.");
            }
            _automaticJumpTrajectoryPending = false;
        }
        catch
        {
            _automaticJumpTrajectoryPending = false;
        }
    }

    private void RestoreAutomaticJumpForce()
    {
        if (!_automaticJumpForceOverridden) return;
        try
        {
            var player = _automaticJumpPlayer;
            // Do not overwrite a simultaneous legitimate game/mod change.  Restore only the exact
            // temporary value this jump applied.
            if (player != null && Math.Abs(player.jumpForce - _automaticJumpAppliedForce) < 0.001f)
                player.jumpForce = _automaticJumpOriginalForce;
        }
        catch { /* Scene transitions rebuild PlayerMovement. */ }
        finally
        {
            _automaticJumpForceOverridden = false;
            _automaticJumpOriginalForce = 0;
            _automaticJumpAppliedForce = 0;
        }
    }

    private bool TryResolvePriorityPickupJump(float now, PlayerMovement player, Camera camera,
        Vector3 playerViewport, out float holdSeconds, out float forceMultiplier, out float predictedApexRise,
        out string reason)
    {
        holdSeconds = 0;
        forceMultiplier = 1f;
        predictedApexRise = float.NaN;
        reason = null;

        if (now >= _nextJumpPriorityPickupScanAt)
        {
            _nextJumpPriorityPickupScanAt = now + 0.03f;
            ScanPriorityPickups(camera, playerViewport);
        }
        if (!_jumpPriorityPickupActive) return false;

        if (!ObserveJumpClosingSpeed(_jumpPriorityPickupTargetId, _jumpPriorityPickupLead, out var closingSpeed))
        {
            reason = $"Locked {_jumpPriorityPickupKind} at {_jumpPriorityPickupLead:0.000} viewport width; waiting for a second position sample";
            return true;
        }

        var targetApexRise = Math.Max(0, _jumpPriorityPickupWorldY - player.transform.position.y + 0.15f);
        var baseForce = Math.Abs(player.jumpForce);
        if (!float.IsFinite(baseForce) || baseForce < 0.01f)
        {
            reason = $"{_jumpPriorityPickupKind} is present, but native jumpForce={player.jumpForce:0.###} is unavailable";
            return true;
        }

        var rigidBody = player.rigidBody;
        var gravity = rigidBody != null
            ? Math.Abs(Physics2D.gravity.y * rigidBody.gravityScale)
            : Math.Abs(Physics2D.gravity.y);
        if (!float.IsFinite(gravity) || gravity < 0.01f) gravity = 0.01f;

        var jumpTime = player.jumpTime;
        if (!float.IsFinite(jumpTime) || jumpTime <= 0.02f) jumpTime = 0.24f;
        var configuredHold = Math.Clamp(Plugin.Config.AutomaticJumpMaximumHoldSeconds.Value, 0.04f, 0.3f);
        var nativePoweredLimit = Math.Clamp(Math.Min(jumpTime, configuredHold), 0.04f, 0.3f);
        var configuredMaximumForce = Math.Clamp(Plugin.Config.AutomaticJumpMaximumForceMultiplier.Value, 1f, 2.5f);
        var physicsMargin = Math.Max(0.015f, Time.fixedDeltaTime);
        var minimumHold = Math.Clamp(physicsMargin * 1.5f, 0.03f, nativePoweredLimit);
        var baseBallisticRise = baseForce * baseForce / (2f * gravity);
        var requiredHoldAtBase = Math.Max(0, (targetApexRise - baseBallisticRise) / baseForce) + physicsMargin;
        if (requiredHoldAtBase <= nativePoweredLimit)
        {
            holdSeconds = Math.Clamp(requiredHoldAtBase, minimumHold, nativePoweredLimit);
        }
        else
        {
            var gTimesT = gravity * nativePoweredLimit;
            var requiredVelocity = -gTimesT + Mathf.Sqrt(gTimesT * gTimesT + 2f * gravity * targetApexRise);
            forceMultiplier = Math.Clamp(requiredVelocity / baseForce, 1f, configuredMaximumForce);
            holdSeconds = nativePoweredLimit;
        }

        var appliedVelocity = baseForce * forceMultiplier;
        predictedApexRise = appliedVelocity * holdSeconds + appliedVelocity * appliedVelocity / (2f * gravity);
        var apexDelay = holdSeconds + appliedVelocity / gravity;
        ResolveJumpMagnetGeometry(player, camera, playerViewport, out var magnetCenterLead,
            out _, out _, out var magnetRadiusViewport);
        var horizontalOvershoot = magnetRadiusViewport > 0
            ? Math.Clamp(magnetRadiusViewport * 0.15f, 0.005f, 0.025f)
            : 0.005f;
        var desiredLeadAtApex = magnetCenterLead - horizontalOvershoot;
        var timeToTargetApex = Math.Max(0, _jumpPriorityPickupLead - desiredLeadAtApex) /
                               Math.Max(0.001f, closingSpeed);
        if (timeToTargetApex > apexDelay + physicsMargin)
        {
            holdSeconds = 0;
            reason = $"{_jumpPriorityPickupKind} reaches target apex in {timeToTargetApex:0.000}s while the current trajectory " +
                     $"needs {apexDelay:0.000}s; staying grounded until the launch line";
            return true;
        }

        reason = $"Highest-priority pickup {_jumpPriorityPickupKind}: lead={_jumpPriorityPickupLead:0.000} viewport width, " +
                 $"target/predicted rise={targetApexRise:0.000}/{predictedApexRise:0.000}, " +
                 $"hold={holdSeconds:0.000}s, force={forceMultiplier:0.00}x";
        if (_jumpPriorityPickupTargetId != 0 &&
            _lastLoggedJumpPriorityPickupTargetId != _jumpPriorityPickupTargetId)
        {
            _lastLoggedJumpPriorityPickupTargetId = _jumpPriorityPickupTargetId;
            SmartLog.Debug(SmartLogCategory.Combat,
                $"Detected a priority minigame pickup: targetId={_jumpPriorityPickupTargetId}, " +
                $"viewport lead={_jumpPriorityPickupLead:0.000}.");
        }
        return true;
    }

    private void ScanPriorityPickups(Camera camera, Vector3 playerViewport)
    {
        _jumpPriorityPickupActive = false;
        _jumpPriorityPickupLead = float.PositiveInfinity;
        _jumpPriorityPickupWorldY = 0;
        _jumpPriorityPickupKind = string.Empty;
        _jumpPriorityPickupTargetId = 0;
        try
        {
            foreach (var pickup in UnityEngine.Object.FindObjectsOfType<ChestHuntKey>())
            {
                if (pickup == null || pickup.gameObject == null || !pickup.gameObject.activeInHierarchy ||
                    !pickup.isActiveAndEnabled) continue;
                ConsiderPriorityPickup(pickup.transform, "Chest Hunt Key", pickup.GetInstanceID(), camera, playerViewport);
            }
            foreach (var pickup in UnityEngine.Object.FindObjectsOfType<ClimbingSoulBoots>())
            {
                if (pickup == null || pickup.gameObject == null || !pickup.gameObject.activeInHierarchy ||
                    !pickup.isActiveAndEnabled) continue;
                ConsiderPriorityPickup(pickup.transform, "Ascending Heights Boots", pickup.GetInstanceID(), camera, playerViewport);
            }
            foreach (var pickup in UnityEngine.Object.FindObjectsOfType<SoulGrappleCape>())
            {
                if (pickup == null || pickup.gameObject == null || !pickup.gameObject.activeInHierarchy ||
                    !pickup.isActiveAndEnabled) continue;
                ConsiderPriorityPickup(pickup.transform, "Grappling Hook Cape", pickup.GetInstanceID(), camera, playerViewport);
            }
        }
        catch { /* Rescan after pooled objects finish spawning or recycling. */ }
    }

    private bool PortalJumpProtectionPresent(float now, Camera camera, PlayerMovement player, out string reason)
    {
        reason = null;
        if (now < _portalGroundReservationUntil)
        {
            reason = "Strategy is preparing a Portal; stopping new jumps and waiting to land";
            return true;
        }
        if (now - _portalRequestedAt < 12f)
        {
            reason = "Strategy just spawned a Portal; staying grounded to enter it overrides every jump target";
            return true;
        }

        if (now >= _nextJumpPortalScanAt)
        {
            _nextJumpPortalScanAt = now + 0.05f;
            _jumpPortalActive = false;
            _jumpPortalLead = float.PositiveInfinity;
            try
            {
                var portal = UnityEngine.Object.FindObjectOfType<Portal>();
                if (portal != null && portal.gameObject != null && portal.gameObject.activeInHierarchy &&
                    portal.isActiveAndEnabled)
                {
                    _jumpPortalActive = true;
                    if (camera != null && player != null)
                    {
                        var playerViewport = camera.WorldToViewportPoint(player.transform.position);
                        var portalViewport = camera.WorldToViewportPoint(portal.transform.position);
                        if (playerViewport.z > 0 && portalViewport.z > 0)
                            _jumpPortalLead = portalViewport.x - playerViewport.x;
                    }
                }
            }
        catch { /* Rescan after pooled-object spawning or a scene transition. */ }
        }

        if (!_jumpPortalActive) return false;
        if (float.IsFinite(_jumpPortalLead) && _jumpPortalLead < -0.12f) return false;
        reason = float.IsFinite(_jumpPortalLead)
            ? $"An active Portal is {_jumpPortalLead:0.000} viewport width ahead; staying grounded to enter"
            : "An active Portal is present; staying grounded to enter";
        return true;
    }

    private void ConsiderPriorityPickup(Transform target, string kind, int targetId, Camera camera,
        Vector3 playerViewport)
    {
        if (target == null) return;
        var viewport = camera.WorldToViewportPoint(target.position);
        if (viewport.z <= 0) return;
        var lead = viewport.x - playerViewport.x;
        if (lead < -0.04f || lead > 0.75f || lead >= _jumpPriorityPickupLead) return;
        _jumpPriorityPickupActive = true;
        _jumpPriorityPickupLead = lead;
        _jumpPriorityPickupWorldY = target.position.y;
        _jumpPriorityPickupKind = kind;
        _jumpPriorityPickupTargetId = targetId;
    }

    private bool TryFindApproachingGiant(Camera camera, Vector3 playerViewport, out float nearestLead)
    {
        nearestLead = float.PositiveInfinity;
        var found = false;
        for (var i = 0; i < _combatEnemyCache.Count; i++)
        {
            try
            {
                var enemyObject = _combatEnemyCache[i];
                if (enemyObject == null || enemyObject.gameObject == null ||
                    !enemyObject.gameObject.activeInHierarchy || !enemyObject.isActiveAndEnabled) continue;
                var stats = enemyObject.enemyStats;
                var enemy = MapEfficiencyEstimator.CurrentStage(stats?.enemy);
                if (stats == null || stats.dead || enemy?.isGiant != true) continue;
                var target = camera.WorldToViewportPoint(enemyObject.transform.position);
                if (target.z <= 0) continue;
                var lead = target.x - playerViewport.x;
                // Reserve almost the entire forward viewport.  The previous 0.34 window detected
                // a Giant only after a high jump was already committed and could not land in time.
                if (lead < -0.03f || lead > 0.85f) continue;
                nearestLead = Math.Min(nearestLead, lead);
                found = true;
            }
            catch { /* Skip objects changing pool state during this cycle. */ }
        }
        return found;
    }

    private bool TryFindPriorityGiant(float now, Camera camera, PlayerMovement player,
        out float nearestLead, out string priorityReason)
    {
        nearestLead = float.PositiveInfinity;
        priorityReason = string.Empty;
        if (player == null || camera == null) return false;

        BaseMap currentMap;
        try { currentMap = MapController.instance?.selectedMap; }
        catch { return false; }
        if (currentMap == null) return false;
        var currentMapId = currentMap.id ?? currentMap.name ?? string.Empty;
        if (now >= _nextPriorityGiantContextScanAt ||
            !string.Equals(currentMapId, _priorityGiantContextMapId, StringComparison.Ordinal))
        {
            _nextPriorityGiantContextScanAt = now + 0.2f;
            _priorityGiantContextMapId = currentMapId;
            var reasons = new List<string>(3);
            var need = _materialNeed;
            if (need != null && need.GiantDrop && need.Current + 0.0001 < need.Desired && need.Map != null &&
                MapEfficiencyEstimator.SameMap(need.Map, currentMap))
            {
                var materialName = DisplayName(need.Material);
                reasons.Add($"missing material {(string.IsNullOrWhiteSpace(materialName) ? "Giant drop" : materialName)}");
            }

            void ConsiderQuestPlan(QuestPlan plan)
            {
                if (plan?.Quest == null || !plan.TargetsGiants || plan.Current >= plan.Goal || plan.Quest.isClaimed ||
                    plan.TargetMap != null && !MapEfficiencyEstimator.SameMap(plan.TargetMap, currentMap)) return;
                var label = $"quest {plan.Name}";
                if (!reasons.Contains(label, StringComparer.Ordinal)) reasons.Add(label);
            }

            ConsiderQuestPlan(_questPlan);
            if (_parallelQuestPlans != null)
                for (var i = 0; i < _parallelQuestPlans.Count; i++) ConsiderQuestPlan(_parallelQuestPlans[i]);
            _priorityGiantContextReason = string.Join(" + ", reasons);
        }
        if (string.IsNullOrEmpty(_priorityGiantContextReason)) return false;

        try
        {
            var playerViewport = camera.WorldToViewportPoint(player.transform.position);
            if (playerViewport.z <= 0) return false;
            RefreshCombatEnemyCache(now);
            if (!TryFindApproachingGiant(camera, playerViewport, out nearestLead)) return false;
            priorityReason = _priorityGiantContextReason;
            return true;
        }
        catch
        {
            nearestLead = float.PositiveInfinity;
            priorityReason = string.Empty;
            return false;
        }
    }

    private bool TryResolveRandomBoxJump(float now, Camera camera, Vector3 playerViewport, float jumpTime,
        out float holdSeconds, out string reason)
    {
        RefreshJumpBoxState(now, camera, playerViewport);

        holdSeconds = 0;
        reason = null;
        if (!_jumpBoxActive) return false;

        // Match the proven upper bound used by the existing automatic box catcher.  Starting a
        // full-height jump earlier can put the player above the box when it reaches the hit line.
        const float launchLead = 0.06f;
        if (_jumpBoxLead > launchLead)
        {
            reason = $"Detected {_jumpBoxKind} at {_jumpBoxLead:0.000} viewport width; staying grounded until box launch line {launchLead:0.000}";
            return true;
        }

        if (_jumpBoxLead < -0.03f)
        {
            reason = $"{_jumpBoxKind} has passed the player; waiting for the next box";
            return true;
        }

        if (!float.IsFinite(jumpTime) || jumpTime <= 0.02f) jumpTime = 0.25f;
        holdSeconds = Math.Clamp(Math.Max(0.25f, jumpTime), 0.25f, 0.3f);
        reason = $"{_jumpBoxKind} entered launch line {_jumpBoxLead:0.000}; full high jump for {holdSeconds:0.00}s";
        if (_jumpBoxTargetId != 0 && _lastLoggedJumpBoxTargetId != _jumpBoxTargetId)
        {
            _lastLoggedJumpBoxTargetId = _jumpBoxTargetId;
            SmartLog.Debug(SmartLogCategory.Combat,
                $"Detected a box jump: targetId={_jumpBoxTargetId}, viewport lead={_jumpBoxLead:0.000}, hold={holdSeconds:0.000}s.");
        }
        return true;
    }

    private bool TryProtectRandomBoxApproach(float now, Camera camera, PlayerMovement player,
        out bool waitForLaunch, out string reason)
    {
        waitForLaunch = false;
        reason = null;
        if (camera == null || player == null) return false;

        var playerViewport = camera.WorldToViewportPoint(player.transform.position);
        if (playerViewport.z <= 0) return false;
        RefreshJumpBoxState(now, camera, playerViewport);
        if (!_jumpBoxActive || _jumpBoxLead < -0.03f || _jumpBoxLead > 1.25f)
            return false;

        const float launchLead = 0.06f;
        waitForLaunch = _jumpBoxLead > launchLead;
        reason = waitForLaunch
            ? $"{_jumpBoxKind} detected {_jumpBoxLead:0.000} viewport width ahead; releasing the current jump early and reserving a grounded launch"
            : $"{_jumpBoxKind} reached its launch line at {_jumpBoxLead:0.000} viewport width";
        return true;
    }

    private void RefreshJumpBoxState(float now, Camera camera, Vector3 playerViewport)
    {
        var nativeBoxCount = 0.0;
        try { nativeBoxCount = Math.Max(0, MapController.instance?.currentSpawnedRandomBoxes ?? 0); }
        catch { /* The active component scan remains authoritative. */ }
        if (nativeBoxCount > 0 && !_jumpBoxActive) _nextJumpBoxScanAt = 0;
        if (now < _nextJumpBoxScanAt) return;

        ScanActiveRandomBoxes(camera, playerViewport);
        _nextJumpBoxScanAt = now + (_jumpBoxActive || nativeBoxCount > 0 ? 0.05f : 0.2f);
    }

    private void ScanActiveRandomBoxes(Camera camera, Vector3 playerViewport)
    {
        _jumpBoxActive = false;
        _jumpBoxLead = float.PositiveInfinity;
        _jumpBoxKind = string.Empty;
        _jumpBoxTargetId = 0;
        try
        {
            foreach (var box in UnityEngine.Object.FindObjectsOfType<RandomBox>())
            {
                if (box == null || box.isHitted || box.gameObject == null || !box.gameObject.activeInHierarchy)
                    continue;
                ConsiderJumpBox(box.transform, "Random Box", box.GetInstanceID(), camera, playerViewport);
            }
            foreach (var box in UnityEngine.Object.FindObjectsOfType<SpecialRandomBox>())
            {
                if (box == null || box.isHitted || box.gameObject == null || !box.gameObject.activeInHierarchy)
                    continue;
                ConsiderJumpBox(box.transform, "Special Random Box", box.GetInstanceID(), camera, playerViewport);
            }
        }
        catch { /* Rescan after the frame that spawns or destroys boxes. */ }
    }

    private void ConsiderJumpBox(Transform target, string kind, int targetId, Camera camera, Vector3 playerViewport)
    {
        if (target == null) return;
        var viewport = camera.WorldToViewportPoint(target.position);
        if (viewport.z <= 0) return;
        var lead = viewport.x - playerViewport.x;
        if (lead < -0.03f || lead > 1.35f || lead >= _jumpBoxLead) return;
        _jumpBoxActive = true;
        _jumpBoxLead = lead;
        _jumpBoxKind = kind;
        _jumpBoxTargetId = targetId;
    }

    private void RefreshJumpCollectableCache(float now)
    {
        var map = MapController.instance?.selectedMap;
        var mapId = map?.id ?? map?.name ?? string.Empty;
        var mapChanged = !string.Equals(mapId, _jumpCollectableCacheMapId, StringComparison.Ordinal);
        if (!mapChanged && _jumpCollectableCache.Count > 0 && now < _nextJumpCollectableCacheRefreshAt) return;

        _jumpCollectableCacheMapId = mapId;
        if (mapChanged) ResetJumpMotionObservation();
        _jumpCollectableCache.Clear();
        _jumpCollectableIds.Clear();
        var pools = CollectablePooler.instance?.pools;
        if (pools != null)
        {
            for (var i = 0; i < pools.Count; i++)
            {
                var parent = pools[i]?.parentObject?.transform;
                if (parent == null) continue;
                for (var childIndex = 0; childIndex < parent.childCount; childIndex++)
                {
                    var child = parent.GetChild(childIndex);
                    var collectable = child?.GetComponent<CollectableGameObject>();
                    if (collectable == null) continue;
                    var id = collectable.GetInstanceID();
                    if (_jumpCollectableIds.Add(id)) _jumpCollectableCache.Add(collectable);
                }
            }
        }
        _nextJumpCollectableCacheRefreshAt = now + (_jumpCollectableCache.Count == 0 ? 1f : mapChanged ? 2f : 30f);
    }

    private bool TryResolveAdaptiveJump(PlayerMovement player, Camera camera, Vector3 playerViewport,
        out float holdSeconds, out float forceMultiplier, out float predictedApexRise, out string reason,
        out bool hasTarget)
    {
        holdSeconds = 0;
        forceMultiplier = 1f;
        predictedApexRise = float.NaN;
        hasTarget = false;
        var nearestLead = float.PositiveInfinity;
        var nearestTargetId = 0;
        for (var i = 0; i < _jumpCollectableCache.Count; i++)
        {
            var collectable = _jumpCollectableCache[i];
            if (TryReadJumpCollectable(collectable, camera, playerViewport, out var lead, out _, out _) &&
                lead < nearestLead)
            {
                nearestLead = lead;
                nearestTargetId = collectable.GetInstanceID();
            }
        }

        var jumpTime = player.jumpTime;
        if (!float.IsFinite(jumpTime) || jumpTime <= 0.02f) jumpTime = 0.24f;
        jumpTime = Math.Clamp(jumpTime, 0.08f, 0.32f);

        if (!float.IsFinite(nearestLead))
        {
            // Blind exploration hops were the main remaining miss source: a new high pattern could
            // spawn while the player was already airborne, at which point its height was detected
            // correctly but could no longer affect the current jump.
            reason = "No active collectable is ahead; staying grounded for the next pattern";
            return false;
        }

        hasTarget = true;

        if (!_jumpTrackingLogged)
        {
            _jumpTrackingLogged = true;
            SmartLog.Debug(SmartLogCategory.Combat,
                $"Locked the first live collectable cluster: cache={_jumpCollectableCache.Count}, " +
                $"initial viewport lead={nearestLead:0.000}.");
        }

        // Build the nearest connected pattern instead of a single narrow horizontal strip.  Arcs
        // start with a low coin and then rise; classifying only the first strip selected a short hop
        // and inevitably missed the high tail.  Chained gaps join that arc while the maximum span
        // prevents a distant second pattern from forcing an unnecessary high jump.
        const float linkedGap = 0.06f;
        const float maximumPatternSpan = 0.32f;
        var clusterEnd = nearestLead + linkedGap;
        for (var pass = 0; pass < 6; pass++)
        {
            var expanded = false;
            for (var i = 0; i < _jumpCollectableCache.Count; i++)
            {
                if (!TryReadJumpCollectable(_jumpCollectableCache[i], camera, playerViewport, out var lead, out _, out _) ||
                    lead > nearestLead + maximumPatternSpan || lead > clusterEnd + linkedGap || lead <= clusterEnd)
                    continue;
                clusterEnd = lead;
                expanded = true;
            }
            if (!expanded) break;
        }

        var highest = float.NegativeInfinity;
        var lowest = float.PositiveInfinity;
        var highestWorldY = float.NegativeInfinity;
        var lowestWorldY = float.PositiveInfinity;
        var targets = 0;
        for (var i = 0; i < _jumpCollectableCache.Count; i++)
        {
            if (!TryReadJumpCollectable(_jumpCollectableCache[i], camera, playerViewport, out var lead, out var height,
                    out var worldY) ||
                lead > clusterEnd + 0.001f) continue;
            highest = Math.Max(highest, height);
            lowest = Math.Min(lowest, height);
            highestWorldY = Math.Max(highestWorldY, worldY);
            lowestWorldY = Math.Min(lowestWorldY, worldY);
            targets++;
        }

        var topRowCount = 0;
        var topRowStart = float.PositiveInfinity;
        var topRowEnd = float.NegativeInfinity;
        const float topRowVerticalTolerance = 0.022f;
        for (var i = 0; i < _jumpCollectableCache.Count; i++)
        {
            if (!TryReadJumpCollectable(_jumpCollectableCache[i], camera, playerViewport, out var lead, out var height,
                    out _) ||
                lead > clusterEnd + 0.001f || height < highest - topRowVerticalTolerance) continue;
            topRowCount++;
            topRowStart = Math.Min(topRowStart, lead);
            topRowEnd = Math.Max(topRowEnd, lead);
        }
        var topRowSpan = topRowCount > 1 ? Math.Max(0, topRowEnd - topRowStart) : 0;
        var sustainedTopRow = topRowCount >= 3 && topRowSpan >= 0.045f;

        if (!ObserveJumpClosingSpeed(nearestTargetId, nearestLead, out var closingSpeed))
        {
            reason = $"Locked the nearest coin pattern at {nearestLead:0.000} viewport width; waiting for a second sample to measure closing speed";
            return false;
        }

        // Balance the entire horizontal pattern rather than only its highest row.  This keeps the
        // ascent over the leading low coins and places the apex near the pattern centre.
        var targetLead = (nearestLead + clusterEnd) * 0.5f;
        ResolveJumpMagnetGeometry(player, camera, playerViewport, out var magnetCenterLead,
            out var magnetCenterWorldY, out var magnetRadiusWorld, out var magnetRadiusViewport);
        // Put the trajectory apex just after the horizontal centre of the top row passes the magnet
        // centre.  The overshoot is a small fraction of the live magnet radius, capped so a huge Rage
        // magnet does not move the apex into the next pattern.
        var horizontalOvershoot = magnetRadiusViewport > 0
            ? Math.Clamp(magnetRadiusViewport * 0.15f, 0.005f, 0.025f)
            : 0.005f;
        var desiredLeadAtApex = magnetCenterLead - horizontalOvershoot;
        var timeToTargetApex = Math.Max(0, targetLead - desiredLeadAtApex) / Math.Max(0.001f, closingSpeed);

        // Native MapController.CoinsSpawner chooses a coin-pattern prefab and spawns each child at:
        //     worldY = patternChildY + Random.Range(1.0f, 2.5f)
        // The active pooled object therefore already contains the exact realised random height.
        // Optimise for the whole vertical envelope.  If the live magnet can cover half the span,
        // place its centre just above the envelope midpoint so neither the top nor bottom row sits at
        // the edge.  If it cannot, use the lowest centre that still reaches the top; lower rows are
        // then collected during ascent.  With no magnet, directly reach the top as before.
        var verticalOvershootViewport = sustainedTopRow ? 0.0125f : 0.008f;
        var verticalOvershootWorld = 0.15f;
        try
        {
            var lower = camera.ViewportToWorldPoint(playerViewport);
            var upper = camera.ViewportToWorldPoint(new Vector3(
                playerViewport.x, playerViewport.y + verticalOvershootViewport, playerViewport.z));
            var projectedOvershoot = Math.Abs(upper.y - lower.y);
            if (float.IsFinite(projectedOvershoot) && projectedOvershoot > 0.001f)
                verticalOvershootWorld = Math.Clamp(projectedOvershoot, 0.08f, 0.6f);
        }
        catch { /* Orthographic cameras convert normally; use conservative world-space margin during transitions. */ }
        var playerWorldY = player.transform.position.y;
        var verticalSpanWorld = Math.Max(0, highestWorldY - lowestWorldY);
        var envelopeMidWorldY = (highestWorldY + lowestWorldY) * 0.5f;
        var centreBias = Math.Min(verticalOvershootWorld, Math.Max(0.05f, verticalSpanWorld * 0.05f));
        float desiredMagnetCenterWorldY;
        var usableMagnetRadius = Math.Max(0, magnetRadiusWorld * 0.85f);
        if (usableMagnetRadius >= verticalSpanWorld * 0.5f + centreBias)
            desiredMagnetCenterWorldY = envelopeMidWorldY + centreBias;
        else if (usableMagnetRadius > 0.05f)
            desiredMagnetCenterWorldY = highestWorldY - usableMagnetRadius + centreBias;
        else
            desiredMagnetCenterWorldY = highestWorldY + verticalOvershootWorld;
        var targetApexRise = Math.Max(0, desiredMagnetCenterWorldY - magnetCenterWorldY);
        var baseForce = Math.Abs(player.jumpForce);
        if (!float.IsFinite(baseForce) || baseForce < 0.01f)
        {
            reason = $"Native jumpForce={player.jumpForce:0.###} is unavailable; staying grounded";
            return false;
        }

        var rigidBody = player.rigidBody;
        var gravity = rigidBody != null
            ? Math.Abs(Physics2D.gravity.y * rigidBody.gravityScale)
            : Math.Abs(Physics2D.gravity.y);
        if (!float.IsFinite(gravity) || gravity < 0.01f) gravity = 0.01f;

        // Native CheckAction writes vertical velocity=jumpForce during the powered interval.  After
        // jumpUp, Rigidbody2D keeps that velocity and gravity brings it to zero.  Therefore:
        // apexRise = velocity * hold + velocity^2 / (2 * gravity)
        // apexDelay = hold + velocity / gravity
        var configuredHold = Math.Clamp(Plugin.Config.AutomaticJumpMaximumHoldSeconds.Value, 0.04f, 0.3f);
        var nativePoweredLimit = Math.Clamp(Math.Min(jumpTime, configuredHold), 0.04f, 0.3f);
        var configuredMaximumForce = Math.Clamp(Plugin.Config.AutomaticJumpMaximumForceMultiplier.Value, 1f, 2.5f);
        var physicsMargin = Math.Max(0.015f, Time.fixedDeltaTime);
        var minimumHold = Math.Clamp(physicsMargin * 1.5f, 0.03f, nativePoweredLimit);
        var baseBallisticRise = baseForce * baseForce / (2f * gravity);
        var requiredHoldAtBase = (targetApexRise - baseBallisticRise) / baseForce;
        // Pointer input is released from Update while the game applies jumpForce from FixedUpdate.
        // Keep one additional physics step so the continuous solution cannot lose its final powered
        // sample merely because the press began just after a physics tick.
        requiredHoldAtBase = Math.Max(0, requiredHoldAtBase) + physicsMargin;

        if (requiredHoldAtBase <= nativePoweredLimit)
        {
            forceMultiplier = 1f;
            holdSeconds = Math.Clamp(requiredHoldAtBase, minimumHold, nativePoweredLimit);
        }
        else
        {
            // Solve H = v*T + v^2/(2g) for v, using the full native powered window.
            var gTimesT = gravity * nativePoweredLimit;
            var requiredVelocity = -gTimesT +
                                   Mathf.Sqrt(gTimesT * gTimesT + 2f * gravity * targetApexRise);
            forceMultiplier = Math.Clamp(requiredVelocity / baseForce, 1f, configuredMaximumForce);
            holdSeconds = nativePoweredLimit;
        }

        var appliedVelocity = baseForce * forceMultiplier;
        var computedApexRise = appliedVelocity * holdSeconds +
                               appliedVelocity * appliedVelocity / (2f * gravity);
        predictedApexRise = computedApexRise;
        var apexDelay = holdSeconds + appliedVelocity / gravity;

        if (timeToTargetApex > apexDelay + physicsMargin)
        {
            reason = $"Top center reaches target apex in {timeToTargetApex:0.000}s while current trajectory needs {apexDelay:0.000}s; " +
                     $"measured speed={closingSpeed:0.000} viewport/s, magnet radius={magnetRadiusWorld:0.000}; waiting";
            return false;
        }

        var profile = forceMultiplier > 1.05f || holdSeconds > 0.18f
            ? "high jump"
            : holdSeconds > 0.08f ? "medium jump" : "short jump";

        reason = $"Pattern targets={targets}, top row={topRowCount}, span={topRowSpan:0.000}; " +
                 $"target apex in {timeToTargetApex:0.000}s, trajectory apex in {apexDelay:0.000}s; " +
                 $"relative group height={lowestWorldY - playerWorldY:0.000}..{highestWorldY - playerWorldY:0.000}; " +
                 $"desired magnet center={desiredMagnetCenterWorldY - playerWorldY:0.000}; " +
                 $"target/actual rise={targetApexRise:0.000}/{computedApexRise:0.000}; " +
                 $"magnet radius={magnetRadiusWorld:0.000} for horizontal tolerance only, speed={closingSpeed:0.000} viewport/s; " +
                 $"{profile} {holdSeconds:0.000}s, force={forceMultiplier:0.00}x";
        if (_loggedJumpProfiles.Add(profile))
            SmartLog.Debug(SmartLogCategory.Combat,
                $"Executed a new adaptive-jump profile: targets={targets}, hold={holdSeconds:0.000}s, " +
                $"force={forceMultiplier:0.00}x, cluster span={clusterEnd - nearestLead:0.000} viewport width.");
        return true;
    }

    private static bool TryReadJumpCollectable(CollectableGameObject collectable, Camera camera,
        Vector3 playerViewport, out float lead, out float height, out float worldY)
    {
        lead = float.PositiveInfinity;
        height = float.NegativeInfinity;
        worldY = float.NegativeInfinity;
        try
        {
            if (collectable == null || collectable.gameObject == null || collectable.pickedUp ||
                !collectable.gameObject.activeInHierarchy || !collectable.isActiveAndEnabled) return false;
            var position = collectable.transform.position;
            var target = camera.WorldToViewportPoint(position);
            if (target.z <= 0) return false;
            lead = target.x - playerViewport.x;
            height = target.y - playerViewport.y;
            worldY = position.y;
            return lead >= -0.025f && lead <= 0.7f && height >= -0.12f && height <= 0.65f;
        }
        catch { return false; }
    }

    private bool ObserveJumpClosingSpeed(int targetId, float lead, out float closingSpeed)
    {
        var sampleAt = Time.time;
        if (targetId != 0 && targetId == _jumpMotionTargetId && _jumpMotionPreviousSampleAt >= 0 &&
            float.IsFinite(_jumpMotionPreviousLead))
        {
            var elapsed = sampleAt - _jumpMotionPreviousSampleAt;
            if (elapsed > 0.001f && elapsed < 0.5f)
            {
                var sample = (_jumpMotionPreviousLead - lead) / elapsed;
                if (float.IsFinite(sample) && sample > 0.005f && sample < 10f)
                    _jumpObservedClosingSpeed = _jumpObservedClosingSpeed <= 0
                        ? sample
                        : Mathf.Lerp(_jumpObservedClosingSpeed, sample, 0.35f);
            }
        }

        _jumpMotionTargetId = targetId;
        _jumpMotionPreviousLead = lead;
        _jumpMotionPreviousSampleAt = sampleAt;
        closingSpeed = _jumpObservedClosingSpeed;
        return closingSpeed > 0.005f && float.IsFinite(closingSpeed);
    }

    private void ResetJumpMotionObservation()
    {
        _jumpMotionTargetId = 0;
        _jumpMotionPreviousLead = float.NaN;
        _jumpMotionPreviousSampleAt = -1f;
        _jumpObservedClosingSpeed = 0;
    }

    private void ResolveJumpMagnetGeometry(PlayerMovement player, Camera camera, Vector3 playerViewport,
        out float centerLead, out float centerWorldY, out float radiusWorld, out float radiusViewport)
    {
        centerLead = 0;
        centerWorldY = player.transform.position.y;
        radiusWorld = 0;
        radiusViewport = 0;
        try
        {
            if ((_jumpCollectableMagnet == null || _jumpCollectableMagnet.gameObject == null ||
                 !_jumpCollectableMagnet.gameObject.activeInHierarchy) &&
                Time.unscaledTime >= _nextJumpMagnetResolveAt)
            {
                _nextJumpMagnetResolveAt = Time.unscaledTime + 5f;
                _jumpCollectableMagnet = UnityEngine.Object.FindObjectOfType<CollectableMagnet>();
            }

            var collider = _jumpCollectableMagnet?.col;
            if (collider == null || !collider.isActiveAndEnabled || collider.radius <= 0) return;
            var scale = collider.transform.lossyScale;
            radiusWorld = collider.radius * Math.Max(Math.Abs(scale.x), Math.Abs(scale.y));
            if (!float.IsFinite(radiusWorld) || radiusWorld <= 0) { radiusWorld = 0; return; }
            var offset = collider.offset;
            var centre = collider.transform.TransformPoint(new Vector3(offset.x, offset.y, 0));
            centerWorldY = centre.y;
            var centreViewport = camera.WorldToViewportPoint(centre);
            var rightViewport = camera.WorldToViewportPoint(centre + Vector3.right * radiusWorld);
            centerLead = centreViewport.x - playerViewport.x;
            radiusViewport = Math.Abs(rightViewport.x - centreViewport.x);
            if (!float.IsFinite(radiusViewport)) radiusViewport = 0;
        }
        catch
        {
            _jumpCollectableMagnet = null;
            _nextJumpMagnetResolveAt = Time.unscaledTime + 1f;
            centerLead = 0;
            centerWorldY = player.transform.position.y;
            radiusWorld = 0;
            radiusViewport = 0;
        }
    }

    internal static bool WindDashAutomationAvailable()
    {
        if (Plugin.Config?.AutomaticCombatEnabled.Value != true) return false;
        try
        {
            var abilities = AbilitiesManager.instance;
            var windDash = abilities?.windDash;
            return SafeWindDashUnlocked(windDash) &&
                   (string.Equals(GetSelectedAbilityName(abilities.selectedAbility), "WindDash",
                        StringComparison.Ordinal) ||
                    Current?._quests?.HasActiveWindDashKillQuest == true);
        }
        catch { return false; }
    }

    internal static bool CanAutomateWithWindDash(Enemy enemy)
    {
        enemy = MapEfficiencyEstimator.CurrentStage(enemy);
        return enemy != null && !enemy.isGiant && !enemy.isFlying && WindDashAutomationAvailable();
    }

    private static bool SafeWindDashActive(WindDash windDash)
    {
        try { return windDash.IsActive(); }
        catch { return false; }
    }

    private static bool SafeWindDashUnlocked(WindDash windDash)
    {
        try { return windDash != null && windDash.Unlocked(); }
        catch { return false; }
    }

    private static bool SafeWindDashReady(WindDash windDash)
    {
        try { return windDash.Unlocked() && !windDash.IsActive() && windDash.GetCurrentCooldown() <= 0.01f; }
        catch { return false; }
    }

    private static double SafeWindDashCooldown(WindDash windDash)
    {
        try { return Math.Max(0, windDash.GetCurrentCooldown()); }
        catch { return double.MaxValue; }
    }

    private void SuspendCombatAfterError(float now, string error)
    {
        _combatSuspendedUntil = now + 5f;
        SetCombatMode(SmartCombatMode.None, "Attack interface failed; entering short backoff", log: false);
        if (now < _nextCombatErrorLogAt) return;
        _nextCombatErrorLogAt = now + 30f;
        SmartLog.Warning(SmartLogCategory.Combat,
            $"Smart combat failed and entered an isolated 5-second backoff; crafting, quests, and travel remain active: {error}");
    }

    private void ClearCombatDecision(string reason)
    {
        _lastCombatVisibleTargets = 0;
        _lastCombatDashTargets = 0;
        _lastCombatReservedTargets = 0;
        _lastCombatBowScore = 0;
        _lastCombatDashScore = 0;
        _lastCombatReason = reason;
        SetCombatMode(SmartCombatMode.None, reason, log: false);
    }

    private void SetCombatMode(SmartCombatMode mode, string reason, bool log = true)
    {
        if (_combatMode == mode) return;
        _combatMode = mode;
        _combatModeChangedAt = Time.unscaledTime;
        if (!log || mode == SmartCombatMode.None ||
            mode == SmartCombatMode.Bow && !Plugin.Config.DebugLogging.Value ||
            Time.unscaledTime < _nextCombatModeLogAt) return;
        _nextCombatModeLogAt = Time.unscaledTime + (Plugin.Config.DebugLogging.Value ? 2f : 30f);
        SmartLog.Debug(SmartLogCategory.Combat, $"Combat mode changed to {mode}.");
    }

    private void SetMovementMode(SmartMovementMode mode, string reason)
    {
        _movementMode = mode;
        _lastMovementReason = reason;
    }

    private void ResetSmartCombat(bool clearCache = false)
    {
        ReleaseAutomaticJump();
        _automaticJumpPlayer = null;
        _automaticJumpTrajectoryPending = false;
        _nativeRageMovementOwned = false;
        _nextCombatDecisionAt = 0;
        _nextBowAttackAt = 0;
        _nextJumpAttemptAt = 0;
        _nextSelectedAbilityCheckAt = 0;
        ResetSelectedAbilityAutomation();
        _nextCombatWorldBlockerScanAt = 0;
        _combatWorldBlocked = false;
        _combatWorldBlockerReason = null;
        SetCombatMode(SmartCombatMode.None, "Smart combat is disabled", log: false);
        SetMovementMode(SmartMovementMode.None, "Automatic movement or smart combat is disabled");
        if (!clearCache) return;
        _combatEnemyCache.Clear();
        _combatEnemyIds.Clear();
        _combatCacheMapId = null;
        _nextCombatCacheRefreshAt = 0;
        _jumpCollectableCache.Clear();
        _jumpCollectableIds.Clear();
        _loggedJumpProfiles.Clear();
        _jumpTrackingLogged = false;
        _jumpCollectableCacheMapId = null;
        _nextJumpCollectableCacheRefreshAt = 0;
        ResetJumpMotionObservation();
        _jumpCollectableMagnet = null;
        _nextJumpMagnetResolveAt = 0;
        _nextJumpBoxScanAt = 0;
        _nextJumpPriorityPickupScanAt = 0;
        _nextJumpPortalScanAt = 0;
        _jumpBoxActive = false;
        _jumpBoxLead = float.PositiveInfinity;
        _jumpBoxKind = string.Empty;
        _jumpBoxTargetId = 0;
        _lastLoggedJumpBoxTargetId = 0;
        _jumpPriorityPickupActive = false;
        _jumpPriorityPickupLead = float.PositiveInfinity;
        _jumpPriorityPickupWorldY = 0;
        _jumpPriorityPickupKind = string.Empty;
        _jumpPriorityPickupTargetId = 0;
        _lastLoggedJumpPriorityPickupTargetId = 0;
        _jumpPortalActive = false;
        _jumpPortalLead = float.PositiveInfinity;
        _automaticJumpTrajectoryPending = false;
        _automaticJumpYieldsToPriorityGiant = false;
        _nextPriorityGiantContextScanAt = 0;
        _priorityGiantContextMapId = string.Empty;
        _priorityGiantContextReason = string.Empty;
    }

    private string CombatStatusSummary() =>
        $"Combat status: enabled={Plugin.Config.AutomaticCombatEnabled.Value}, movement mode={_movementMode}, combat mode={_combatMode}, " +
        $"auto jump/regular Boost={Plugin.Config.AutomaticJumpEnabled.Value}/{Plugin.Config.AutomaticBoostEnabled.Value}, enemy cache={_combatEnemyCache.Count}, " +
        $"collectable cache={_jumpCollectableCache.Count}, visible targets={_lastCombatVisibleTargets}, " +
        $"dash/reserved targets={_lastCombatDashTargets}/{_lastCombatReservedTargets}, " +
        $"bow/Wind Dash score={_lastCombatBowScore:0.##}/{_lastCombatDashScore:0.##}";

    private readonly struct CombatTarget
    {
        internal readonly Enemy Enemy;
        internal readonly double Utility;
        internal readonly bool ArrowCompatible;
        internal readonly bool DashCompatible;
        internal readonly bool InDashWindow;
        internal readonly bool InDashReservationWindow;
        internal readonly bool IsShiny;

        internal CombatTarget(Enemy enemy, double utility, bool arrowCompatible, bool dashCompatible,
            bool inDashWindow, bool inDashReservationWindow, bool isShiny)
        {
            Enemy = enemy;
            Utility = utility;
            ArrowCompatible = arrowCompatible;
            DashCompatible = dashCompatible;
            InDashWindow = inDashWindow;
            InDashReservationWindow = inDashReservationWindow;
            IsShiny = isShiny;
        }
    }
}

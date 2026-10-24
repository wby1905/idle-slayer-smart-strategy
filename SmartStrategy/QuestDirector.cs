using Il2Cpp;
using System.Globalization;
using UnityEngine;

namespace SmartStrategy;

internal sealed partial class QuestDirector
{
    private readonly CraftableCatalog _catalog;
    private readonly List<QuestEntry> _snapshot = new();
    private readonly Dictionary<int, int> _rerollFailures = new();
    private readonly Dictionary<int, float> _deferredUntil = new();
    private float _nextScan;
    private float _nextMutation;
    private int _lockedQuestId;
    private int _lockedProgress;
    private float _lockedProgressAt;
    private int _dailyRerolls;
    private int _dailyRerollsSinceClaim;
    private int _weeklyRerolls;
    private int _dailyMissingScans;
    private int _weeklyMissingScans;
    private int _dailyRerollChain;
    private int _weeklyRerollChain;
    private float _nextDailyRerollAt;
    private float _nextWeeklyRerollAt;
    private int _unavailableDailyCount;
    private int _unavailableWeeklyCount;
    private int _lastLoggedUnavailableDaily = -1;
    private int _lastLoggedUnavailableWeekly = -1;
    private float _nextDailyRegenerateAt;
    private float _nextWeeklyRegenerateAt;
    private float _nextPortfolioScan;
    private PortfolioMapChoice _portfolio;
    private int _portfolioRevision;
    private int _snapshotPlanRevision;
    private string _snapshotPlanSignature = string.Empty;
    private readonly Dictionary<int, QuestPlan> _planCache = new();
    private int _planCacheSnapshotRevision = -1;
    private int _planCachePortfolioRevision = -1;
    private IReadOnlyList<QuestPlan> _parallelPlanCache = Array.Empty<QuestPlan>();
    private int _parallelPlanPrimaryId;
    private int _parallelPlanSnapshotRevision = -1;
    private int _parallelPlanPortfolioRevision = -1;
    private string _parallelPlanMapKey = string.Empty;
    private float _nextPrimaryPreemptionScan;
    private string _lastPortfolioRouteId;
    private readonly Dictionary<int, QuestProgressObservation> _progressObservations = new();
    private readonly HashSet<int> _stalledDailyProtectionLogged = new();
    private float _nextActiveTimeTick;
    private float _lastActiveTimeTick = -1f;
    private readonly Dictionary<QuestKind, QuestRuntimeCalibration> _runtimeCalibrations = new();
    private bool _runtimeCalibrationDirty;
    private float _nextRuntimeCalibrationPersistAt;
    private readonly Dictionary<string, int> _strategicUnlockBonuses = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int> _questChainUnlockBonuses = new();
    private float _nextStrategicUnlockScan;
    private float _questStateSettleUntil;
    private readonly HashSet<int> _dailyGeneratedRerollCandidates = new();
    private readonly HashSet<int> _weeklyGeneratedRerollCandidates = new();
    private float _dailyGeneratedReadyAt;
    private float _weeklyGeneratedReadyAt;
    private HashSet<int> _dailyReplacementBaseline;
    private HashSet<int> _weeklyReplacementBaseline;
    private int _dailyReplacementOriginalId;
    private int _weeklyReplacementOriginalId;
    private float _dailyReplacementDeadline;
    private float _weeklyReplacementDeadline;
    private float _nextRerollPermissionLog;
    private bool _generationTrackingInitialized;
    private float _dailyPostClaimReviewAt;
    private float _weeklyPostClaimReviewAt;
    private bool _weeklyPreferredTargetLogged;
    private bool _weeklyTargetStageInitialized;
    private QuestKind _weeklyPreferredTarget = QuestKind.CompleteDailyQuests;
    private readonly HashSet<int> _keptDailyReachableKillIds = new();
    private readonly HashSet<int> _keptDailyRageOpportunityIds = new();
    private readonly HashSet<int> _keptDailyPassiveCollectableIds = new();
    private readonly HashSet<int> _keptDailyParallelMaterialIds = new();
    private readonly HashSet<int> _keptDailyCraftableIds = new();
    private float _nextZyniumUnavailableLogAt;

    internal QuestDirector(CraftableCatalog catalog)
    {
        _catalog = catalog;
        LoadRuntimeCalibrations();
    }

    internal int DailyRerolls => _dailyRerolls;
    internal int DailyRerollsSinceClaim => _dailyRerollsSinceClaim;
    internal int WeeklyRerolls => _weeklyRerolls;
    internal int UnavailableDailyCount => _unavailableDailyCount;
    internal int UnavailableWeeklyCount => _unavailableWeeklyCount;

    internal bool HasActiveDailyTasks => _snapshot.Any(entry => entry?.Source == QuestSource.Daily &&
                                                                 IsActionable(entry));

    internal bool HasActiveWindDashKillQuest => _snapshot.Any(entry =>
        IsActionable(entry) && entry.Quest.GetKind() == QuestKind.WindDashKills);

    internal void TickActiveTime()
    {
        var now = Time.unscaledTime;
        if (now < _nextActiveTimeTick) return;
        _nextActiveTimeTick = now + RuntimeCadence.QuestActiveTimeSampleSeconds;
        if (_lastActiveTimeTick < 0)
        {
            _lastActiveTimeTick = now;
            return;
        }
        var delta = Math.Clamp(now - _lastActiveTimeTick, 0, 2f);
        _lastActiveTimeTick = now;
        if (delta <= 0) return;

        foreach (var entry in _snapshot)
        {
            var quest = entry?.Quest;
            if (quest == null || quest.isClaimed || !QuestClockActive(quest)) continue;
            try
            {
                if (_progressObservations.TryGetValue(quest.GetInstanceID(), out var observation))
                    observation.ActiveElapsedSeconds += delta;
            }
        catch { /* The next scan rebuilds quest objects invalidated by a scene transition. */ }
        }
    }

    private static bool QuestClockActive(Quest quest)
    {
        if (quest == null) return false;
        try
        {
            return quest.GetKind() switch
            {
                QuestKind.BonusStage or QuestKind.BonusStageSections => GameState.IsBonus(),
                QuestKind.ChestHuntChests => GameState.IsChestHunt(),
                QuestKind.CompleteAscendingHeights => GameState.IsAscendingHeights(),
                QuestKind.CompleteGrappleRuns => CraftableCatalog.Normalize(
                    (MapController.instance?.selectedMap?.id ?? string.Empty) +
                    (MapController.instance?.selectedMap?.name ?? string.Empty)).Contains("grapple"),
                _ => GameState.current is GameStates.RunnerMode or GameStates.RageMode or GameStates.BattleMode
            };
        }
        catch { return false; }
    }

    internal Quest FindActiveMaterialCollectionQuest()
    {
        foreach (var entry in _snapshot)
            if (entry?.Quest?.GetKind() == QuestKind.GetMaterials && IsActionable(entry)) return entry.Quest;
        return null;
    }

    internal Quest FindActiveCraftTemporaryItemsQuest()
    {
        return _snapshot.Where(entry => entry?.Quest?.GetKind() == QuestKind.CraftTemporaryItems &&
                                        IsActionable(entry))
            .OrderBy(entry => entry.Source == QuestSource.Daily ? 0 :
                              entry.Source == QuestSource.Weekly ? 1 : 2)
            .ThenBy(entry => Math.Max(0, entry.Quest.questGoal - entry.Quest.questCurrentGoal))
            .Select(entry => entry.Quest)
            .FirstOrDefault();
    }

    internal IReadOnlyList<QuestPlan> FindParallelPlans(QuestPlan primaryPlan)
    {
        if (_snapshot.Count == 0) return Array.Empty<QuestPlan>();
        var currentMap = MapController.instance?.selectedMap;
        var mapKey = $"{MapCacheKey(currentMap)}>{MapCacheKey(primaryPlan?.TargetMap)}";
        if (_parallelPlanPrimaryId == (primaryPlan?.InstanceId ?? 0) &&
            _parallelPlanSnapshotRevision == _snapshotPlanRevision &&
            _parallelPlanPortfolioRevision == _portfolioRevision &&
            string.Equals(_parallelPlanMapKey, mapKey, StringComparison.Ordinal))
            return _parallelPlanCache;

        var plans = new List<QuestPlan>();
        foreach (var entry in _snapshot)
        {
            if (!IsActionable(entry) || entry.Quest.GetInstanceID() == primaryPlan?.InstanceId) continue;
            var candidate = MakePlanCached(entry, _portfolio);
            if (candidate == null || !SupportsParallelExecution(candidate)) continue;

        // Parallel quests never own travel. A map-bound quest enters the execution group only when the current
        // composite route already progresses it; map-agnostic crafting, Rage, and collection quests may ride along.
            if (candidate.TargetMap != null &&
                !MapEfficiencyEstimator.SameMap(candidate.TargetMap, currentMap) &&
                !MapEfficiencyEstimator.SameMap(candidate.TargetMap, primaryPlan?.TargetMap))
                continue;
            plans.Add(candidate);
        }

        _parallelPlanCache = plans.OrderBy(ParallelExecutionPriority)
            .ThenByDescending(plan => SourcePriorityRank(plan.Source))
            .ThenByDescending(plan => plan.Score)
            .ThenBy(plan => plan.InstanceId)
            .ToArray();
        _parallelPlanPrimaryId = primaryPlan?.InstanceId ?? 0;
        _parallelPlanSnapshotRevision = _snapshotPlanRevision;
        _parallelPlanPortfolioRevision = _portfolioRevision;
        _parallelPlanMapKey = mapKey;
        return _parallelPlanCache;
    }

    private static bool SupportsParallelExecution(QuestPlan plan) => plan.Type is
        QuestKind.KillEnemies or QuestKind.KillEnemiesWithArrows or QuestKind.KillFlyers or
        QuestKind.KillGiants or QuestKind.KillEnemiesWithRageMode or QuestKind.KillEnemiesWithCharacter or
        QuestKind.KillEnemiesOfType or QuestKind.KillEnemiesInLocation or QuestKind.KillShinyEnemies or QuestKind.DoCriticals or
        QuestKind.GetMaterials or QuestKind.CraftTemporaryItems or QuestKind.UseRageMode or
        QuestKind.PickUpCoins or QuestKind.PickUpGemstones or QuestKind.PickUpDiamonds or
        QuestKind.PickUpEmeralds or QuestKind.PickUpRubies or QuestKind.PickUpSapphires or
        QuestKind.PickUpZyniums or QuestKind.HitRandomBoxes or QuestKind.HitRandomSilverBoxes or
        QuestKind.HitRandomGoldenBoxes or QuestKind.GetCoinsFromRandomBox;

    private static int ParallelExecutionPriority(QuestPlan plan)
    {
        if (plan.Type == QuestKind.KillEnemiesWithRageMode) return 0;
        if (plan.Type == QuestKind.UseRageMode || plan.RequiresRageForBowAutomation) return 1;
        if (plan.Type == QuestKind.CraftTemporaryItems) return 2;
        if (IsSpecificKillObjective(plan)) return 3;
        if (plan.Type == QuestKind.GetMaterials) return 4;
        return 5;
    }

    internal bool TickMaintenance()
    {
        if (QuestManager.instance == null || QuestTypes.list == null) return false;
        var now = Time.unscaledTime;
        if (now < _questStateSettleUntil)
        {
        // Freeze only the replaced slot while stable quests continue. Clearing the whole snapshot for five seconds
        // made existing kill quests idle for minutes during long reroll chains.
            return false;
        }
        if (now < _nextScan) return false;
        _nextScan = now + Math.Max(0.5f, Plugin.Config.QuestScanIntervalSeconds.Value);
        RefreshSnapshot();
        UpdateGeneratedRerollCandidates(now);

        if (Plugin.Config.AutoClaimQuests.Value && TryClaimOne())
        {
            _nextMutation = now + RuntimeCadence.QuestMutationSettleSeconds;
            return false;
        }
        if (now < _nextMutation) return false;

        if (TryRegenerateMissing())
        {
            _nextMutation = now + RuntimeCadence.QuestGenerationSettleSeconds;
            return false;
        }
        if (TryRerollOne())
        {
            _nextMutation = now + 2.5f;
            return false;
        }
        return false;
    }

    private void UpdateGeneratedRerollCandidates(float now)
    {
        if (!_generationTrackingInitialized)
        {
            while (QuestGenerationBridge.TryDequeueDaily(out _)) { }
            while (QuestGenerationBridge.TryDequeueWeekly(out _)) { }
            _generationTrackingInitialized = true;
            foreach (var id in QuestGenerationBridge.ActiveDailyIds()) _dailyGeneratedRerollCandidates.Add(id);
            foreach (var id in QuestGenerationBridge.ActiveWeeklyIds()) _weeklyGeneratedRerollCandidates.Add(id);
            _dailyGeneratedReadyAt = now + RuntimeCadence.QuestGenerationSettleSeconds;
            _weeklyGeneratedReadyAt = now + RuntimeCadence.QuestGenerationSettleSeconds;
            InitializeWeeklyTargetStage();
            SmartLog.Debug(SmartLogCategory.Quest,
                $"Reroll baseline initialized: existing Daily={_dailyGeneratedRerollCandidates.Count}, Weekly={_weeklyGeneratedRerollCandidates.Count}; " +
                $"waiting {RuntimeCadence.QuestGenerationSettleSeconds:0.#}s before evaluation.");
            return;
        }
        while (QuestGenerationBridge.TryDequeueDaily(out var dailyIds))
        {
            _dailyGeneratedRerollCandidates.Clear();
            foreach (var id in dailyIds ?? Array.Empty<int>()) _dailyGeneratedRerollCandidates.Add(id);
            _dailyRerollChain = 0;
            _dailyRerollsSinceClaim = 0;
            _dailyReplacementBaseline = null;
            _dailyReplacementOriginalId = 0;
            _dailyReplacementDeadline = 0f;
            _dailyGeneratedReadyAt = now + RuntimeCadence.QuestGenerationSettleSeconds;
            SmartLog.Debug(SmartLogCategory.Quest,
                $"Detected {_dailyGeneratedRerollCandidates.Count} newly generated Daily quests; " +
                $"waiting {RuntimeCadence.QuestGenerationSettleSeconds:0.#}s before reroll evaluation.");
        }
        while (QuestGenerationBridge.TryDequeueWeekly(out var weeklyIds))
        {
            _weeklyGeneratedRerollCandidates.Clear();
            foreach (var id in weeklyIds ?? Array.Empty<int>()) _weeklyGeneratedRerollCandidates.Add(id);
            _weeklyRerollChain = 0;
            _weeklyReplacementBaseline = null;
            _weeklyReplacementOriginalId = 0;
            _weeklyReplacementDeadline = 0f;
            _weeklyGeneratedReadyAt = now + RuntimeCadence.QuestGenerationSettleSeconds;
            SetWeeklyPreferredTarget(QuestKind.CompleteDailyQuests,
                $"Detected {_weeklyGeneratedRerollCandidates.Count} quests in a new Weekly set; restart from Complete Daily Quests");
            SmartLog.Debug(SmartLogCategory.Quest,
                $"Detected a newly generated Weekly set; waiting {RuntimeCadence.QuestGenerationSettleSeconds:0.#}s before reroll evaluation.");
        }

        var activeDaily = QuestGenerationBridge.ActiveDailyIds();
        var activeWeekly = QuestGenerationBridge.ActiveWeeklyIds();
        RearmAfterClaim(activeDaily, _dailyGeneratedRerollCandidates, ref _dailyPostClaimReviewAt,
            ref _dailyGeneratedReadyAt, ref _dailyRerollChain, now, "Daily");
        RearmAfterClaim(activeWeekly, _weeklyGeneratedRerollCandidates, ref _weeklyPostClaimReviewAt,
            ref _weeklyGeneratedReadyAt, ref _weeklyRerollChain, now, "Weekly");
        _dailyGeneratedRerollCandidates.RemoveWhere(id => !activeDaily.Contains(id));
        _weeklyGeneratedRerollCandidates.RemoveWhere(id => !activeWeekly.Contains(id));

        ResolveReplacementCandidates(activeDaily, ref _dailyReplacementBaseline,
            ref _dailyReplacementOriginalId, ref _dailyReplacementDeadline,
            _dailyGeneratedRerollCandidates, now, "Daily");
        ResolveReplacementCandidates(activeWeekly, ref _weeklyReplacementBaseline,
            ref _weeklyReplacementOriginalId, ref _weeklyReplacementDeadline,
            _weeklyGeneratedRerollCandidates, now, "Weekly");
    }

    private static void RearmAfterClaim(HashSet<int> active, HashSet<int> candidates, ref float reviewAt,
        ref float readyAt, ref int rerollChain, float now, string label)
    {
        if (reviewAt <= 0f || now < reviewAt) return;
        reviewAt = 0f;
        candidates.Clear();
        foreach (var id in active) candidates.Add(id);
        rerollChain = 0;
        readyAt = now;
        if (candidates.Count > 0)
            SmartLog.Debug(SmartLogCategory.Quest,
                $"A claimed quest released its non-repeat type pool; reevaluating {candidates.Count} remaining candidates.");
    }

    private static void ResolveReplacementCandidates(HashSet<int> active, ref HashSet<int> baseline,
        ref int originalId, ref float deadline, HashSet<int> candidates, float now, string label)
    {
        if (baseline == null) return;
        var baselineSnapshot = baseline;
        var added = active.Where(id => !baselineSnapshot.Contains(id)).ToArray();
        if (added.Length > 0)
        {
            foreach (var id in added) candidates.Add(id);
            baseline = null;
            originalId = 0;
            deadline = 0f;
            return;
        }
        if (now < deadline) return;
        if (originalId != 0 && active.Contains(originalId))
        {
            // Idle Slayer may rewrite type and target on the existing Daily or Weekly object instead of replacing it.
            // InstanceID then remains unchanged, so no new ID does not mean failure. Return the slot to evaluation
            // after native state settles.
            candidates.Add(originalId);
            SmartLog.Debug(SmartLogCategory.Quest,
                "A reroll reused the original quest object ID; the slot returned to evaluation after native state settled.");
        }
        else
        {
            SmartLog.Warning(SmartLogCategory.Quest,
                "The replacement quest was not visible before the settle deadline; stopping this reroll chain until the next generation event.");
        }
        baseline = null;
        originalId = 0;
        deadline = 0f;
    }

    internal QuestPlan SelectPlan()
    {
        if (_snapshot.Count == 0) return null;
        var now = Time.unscaledTime;
        if (now >= _nextPortfolioScan)
        {
        // Remaining progress does not immediately change quest identity, targets, or reachable maps. Generation,
        // completion, map changes, and event end explicitly invalidate this timer; otherwise use a low-frequency fallback.
            _nextPortfolioScan = now + Math.Max(30f, Plugin.Config.QuestScanIntervalSeconds.Value);
            _portfolio = ResolvePortfolioMap();
            _portfolioRevision++;
            InvalidatePlanCaches();
        }
        var portfolio = _portfolio;
        QuestEntry locked = null;
        QuestPlan lockedPlan = null;
        if (_lockedQuestId != 0)
        {
            locked = _snapshot.FirstOrDefault(x => x.Quest != null && x.Quest.GetInstanceID() == _lockedQuestId && IsActionable(x));
            if (locked != null)
            {
                lockedPlan = MakePlanCached(locked, portfolio);
                if (lockedPlan == null || IsBackgroundOnlyOpportunity(lockedPlan.Type))
                {
                    if (lockedPlan != null)
                        SmartLog.Debug(SmartLogCategory.Quest,
                            $"Released background-only quest type={lockedPlan.Type} from the primary lock.");
                    _lockedQuestId = 0;
                    locked = null;
                    lockedPlan = null;
                }
                else
                {
                    QuestPlan preemption = null;
                    if (now >= _nextPrimaryPreemptionScan)
                    {
                        _nextPrimaryPreemptionScan = now + Math.Max(2f,
                            Plugin.Config.QuestScanIntervalSeconds.Value);
                        preemption = FindPrimaryPreemption(lockedPlan, portfolio);
                    }
                    if (preemption != null)
                    {
                        var higherTier = PrimaryTier(preemption) > PrimaryTier(lockedPlan);
                        var strongerUnlock = preemption.StrategicUnlockBonus > lockedPlan.StrategicUnlockBonus;
                        SmartLog.Info(SmartLogCategory.Quest,
                            higherTier
                                ? $"Primary quest changed: source={preemption.Source}, type={preemption.Type} outranks type={lockedPlan.Type}."
                                : strongerUnlock
                                    ? $"Strategic unlock preemption: type={preemption.Type}, bonus={preemption.StrategicUnlockBonus} " +
                                      $"outranks type={lockedPlan.Type}, bonus={lockedPlan.StrategicUnlockBonus}."
                                    : $"Persistent quest priority: type={preemption.Type} took the same-tier primary slot; type={lockedPlan.Type} remains in the joint route.");
                        return ActivatePlan(preemption, now, true);
                    }
                }
            }
            if (locked != null)
            {
                var progress = locked.Quest.questCurrentGoal;
                if (progress > _lockedProgress)
                {
                    _lockedProgress = progress;
                    _lockedProgressAt = now;
                }
                var stalled = now - _lockedProgressAt >= Math.Max(20f, Plugin.Config.QuestStallSeconds.Value);
                if (!stalled) return lockedPlan;
                SmartLog.Info(SmartLogCategory.Quest,
                    $"Quest stalled: type={locked.Quest.GetKind()}, instanceId={locked.Quest.GetInstanceID()}, " +
                    $"noProgressFor={now - _lockedProgressAt:0}s; selecting a new primary quest.");
                _deferredUntil[_lockedQuestId] = now + Math.Max(60f, Plugin.Config.QuestStallSeconds.Value);
                // The portfolio built at the start of this cycle still contains the newly deferred quest. Recompute it
                // immediately or stale composite value may drag the replacement back to the same map for up to 30 seconds.
                _portfolio = ResolvePortfolioMap();
                _portfolioRevision++;
                InvalidatePlanCaches();
                portfolio = _portfolio;
            }
            _lockedQuestId = 0;
        }

        QuestPlan best = null;
        var bestTier = -1;
        foreach (var entry in _snapshot)
        {
            if (!IsActionable(entry)) continue;
            var id = entry.Quest.GetInstanceID();
            if (IsDeferredFromPrimary(entry.Quest, now)) continue;
            var candidate = MakePlanCached(entry, portfolio);
            if (candidate == null) continue;
            var tier = PrimaryTier(candidate);
            if (tier < 0) continue;
            if (tier > bestTier || tier == bestTier && (best == null || candidate.Score > best.Score))
            {
                best = candidate;
                bestTier = tier;
            }
        }
        // Avoid permanent idle when only one executable quest exists: a deferred quest remains a fallback but never
        // blocks an available replacement.
        if (best == null)
        {
            bestTier = -1;
            foreach (var entry in _snapshot)
            {
                if (!IsActionable(entry)) continue;
                var candidate = MakePlanCached(entry, portfolio);
                if (candidate == null) continue;
                var tier = PrimaryTier(candidate);
                if (tier < 0) continue;
                if (tier > bestTier || tier == bestTier && (best == null || candidate.Score > best.Score))
                {
                    best = candidate;
                    bestTier = tier;
                }
            }
        }
        if (best == null) return null;
        return ActivatePlan(best, now, true);
    }

    private QuestPlan FindPrimaryPreemption(QuestPlan lockedPlan, PortfolioMapChoice portfolio)
    {
        QuestPlan best = null;
        var lockedTier = PrimaryTier(lockedPlan);
        foreach (var entry in _snapshot)
        {
            if (!IsActionable(entry) || entry.Quest.GetInstanceID() == lockedPlan.InstanceId) continue;
            if (IsDeferredFromPrimary(entry.Quest, Time.unscaledTime))
                continue; // A stalled quest remains parallel during backoff and cannot retake primary focus.
            var candidate = MakePlanCached(entry, portfolio);
            if (candidate == null) continue;
            var candidateTier = PrimaryTier(candidate);
            if (candidateTier < 0) continue;
            if (candidateTier < lockedTier) continue;
            if (candidateTier == lockedTier)
            {
                if (candidate.StrategicUnlockBonus < lockedPlan.StrategicUnlockBonus) continue;
                if (candidate.StrategicUnlockBonus == lockedPlan.StrategicUnlockBonus &&
                    SourcePriorityRank(candidate.Source) <= SourcePriorityRank(lockedPlan.Source)) continue;
            }
            if (best == null || candidateTier > PrimaryTier(best) ||
                candidateTier == PrimaryTier(best) && candidate.StrategicUnlockBonus > best.StrategicUnlockBonus ||
                candidateTier == PrimaryTier(best) && candidate.StrategicUnlockBonus == best.StrategicUnlockBonus &&
                SourcePriorityRank(candidate.Source) > SourcePriorityRank(best.Source) ||
                candidateTier == PrimaryTier(best) && candidate.StrategicUnlockBonus == best.StrategicUnlockBonus &&
                SourcePriorityRank(candidate.Source) == SourcePriorityRank(best.Source) && candidate.Score > best.Score)
                best = candidate;
        }
        return best;
    }

    private int PrimaryTier(QuestPlan plan)
    {
        if (plan == null) return -1;
        if (IsBackgroundOnlyOpportunity(plan.Type)) return -1;
        if (plan.StrategicUnlockBonus > 0) return 5;
        // A Portal quest preempts only when cooldown is actually ready; execution releases its lock immediately after
        // opening. While cooling down it stays low priority and cannot leave kill or material quests waiting.
        if (plan.Source == QuestSource.Weekly && plan.Type == QuestKind.GoThroughPortals &&
            Plugin.Config.WeeklyPreferredPairStrategy.Value &&
            _weeklyPreferredTarget == QuestKind.GoThroughPortals && PortalReadyForWeeklyQuest()) return 4;
        if (plan.Type == QuestKind.VisitLocation) return PortalReadyForWeeklyQuest() ? 3 : 0;
        if (IsSpecificKillObjective(plan)) return 3;
        if (plan.Type == QuestKind.GetMaterials) return 2;
        // Unspecified enemy, bow, character, Rage, and Shiny kills all progress while pursuing concrete targets or materials.
        if (NeedsEnemyMap(plan.Type)) return 1;
        if (plan.TargetMap != null) return 1;
        return 0;
    }

    private static bool IsBackgroundOnlyOpportunity(QuestKind type) => type is
        QuestKind.HitRandomBoxes or QuestKind.HitRandomSilverBoxes or
        QuestKind.HitRandomGoldenBoxes or QuestKind.GetCoinsFromRandomBox or
        QuestKind.DoCriticals;

    private static bool IsSpecificKillObjective(QuestPlan plan)
    {
        if (plan?.Quest == null) return false;
        if (plan.Type is QuestKind.KillFlyers or QuestKind.KillGiants or QuestKind.KillEnemiesOfType or
            QuestKind.KillEnemiesInLocation) return true;
        try { return plan.Quest.enemyToKill != null || plan.Quest.enemyType != null; }
        catch { return false; }
    }

    private static bool PortalReadyForWeeklyQuest()
    {
        try
        {
            var portal = PortalButton.instance;
            return portal != null && portal.currentCd <= 0 && portal.isActiveAndEnabled &&
                   portal.gameObject != null && portal.gameObject.activeInHierarchy;
        }
        catch { return false; }
    }

    private bool IsDeferredFromPrimary(Quest quest, float now)
    {
        if (quest == null) return false;
        try
        {
            var id = quest.GetInstanceID();
            if (!_deferredUntil.TryGetValue(id, out var until)) return false;
            if (now < until) return true;
            _deferredUntil.Remove(id);
            return false;
        }
        catch { return false; }
    }

    private string PrimaryLabel(QuestPlan plan) => PrimaryTier(plan) switch
    {
        5 => "Strategic unlock",
        4 => "Portal opportunity",
        3 => "Specified kill",
        2 => "Active progress",
        1 => "Opportunistic kill",
        _ => "Passive"
    };

    private QuestPlan ActivatePlan(QuestPlan plan, float now, bool logTarget)
    {
        _lockedQuestId = plan.InstanceId;
        _lockedProgress = plan.Current;
        _lockedProgressAt = now;
        if (logTarget)
        SmartLog.Info(SmartLogCategory.Quest,
            $"Quest plan: source={plan.Source}, instanceId={plan.InstanceId}, progress={plan.Current}/{plan.Goal}, type={plan.Type}, " +
                $"map={plan.TargetMap?.name ?? "any"}, expected={FormatDuration(plan.ExpectedSeconds)}, density={plan.TargetEnemiesPerPattern:0.##}/pattern, " +
                $"map utility={plan.MapUtility:0.###}, joint quests={plan.JointQuestCount}, unlock bonus={plan.StrategicUnlockBonus}, " +
                $"targets Giants={plan.TargetsGiants}, item rule={QuestEffectPolicy.Describe(plan.Quest)}, " +
                $"Rage required for bow={plan.RequiresRageForBowAutomation}, score={plan.Score}.");
        return plan;
    }

    internal void ReleasePlan(string reason)
    {
        if (_lockedQuestId != 0) SmartLog.Debug(SmartLogCategory.Quest, "Released the current quest lock.");
        _lockedQuestId = 0;
    }

    internal void RequestImmediateReplan(string reason)
    {
        ReleasePlan(reason);
        _nextScan = 0f;
        _nextPortfolioScan = 0f;
        _nextPrimaryPreemptionScan = 0f;
        InvalidatePlanCaches();
    }

    internal void HoldPlanDuringMaterialRecovery()
    {
        if (_lockedQuestId != 0) _lockedProgressAt = Time.unscaledTime;
    }

    internal void SuspendAfterFailure(float retryAt)
    {
        _snapshot.Clear();
        _lockedQuestId = 0;
        _portfolio = null;
        _nextScan = retryAt;
        _questStateSettleUntil = retryAt;
        _nextPortfolioScan = 0;
        _snapshotPlanSignature = string.Empty;
        _snapshotPlanRevision++;
        InvalidatePlanCaches();
    }

    private QuestPlan MakePlanCached(QuestEntry entry, PortfolioMapChoice portfolio)
    {
        if (entry?.Quest == null) return null;
        if (_planCacheSnapshotRevision != _snapshotPlanRevision ||
            _planCachePortfolioRevision != _portfolioRevision)
        {
            _planCache.Clear();
            _planCacheSnapshotRevision = _snapshotPlanRevision;
            _planCachePortfolioRevision = _portfolioRevision;
        }

        var id = entry.Quest.GetInstanceID();
        if (_planCache.TryGetValue(id, out var cached)) return cached;
        var plan = MakePlan(entry, portfolio);
        _planCache[id] = plan;
        return plan;
    }

    private void InvalidatePlanCaches()
    {
        _planCache.Clear();
        _planCacheSnapshotRevision = -1;
        _planCachePortfolioRevision = -1;
        _parallelPlanCache = Array.Empty<QuestPlan>();
        _parallelPlanPrimaryId = 0;
        _parallelPlanSnapshotRevision = -1;
        _parallelPlanPortfolioRevision = -1;
        _parallelPlanMapKey = string.Empty;
    }

    private void UpdateSnapshotPlanRevision()
    {
        var parts = new List<string>();
        foreach (var entry in _snapshot)
        {
            var quest = entry?.Quest;
            if (quest == null) continue;
            try
            {
                var actionable = IsActionable(entry) ? 1 : 0;
                var enemyId = quest.enemyToKill?.GetInstanceID() ?? 0;
                var enemyTypeId = quest.enemyType?.GetInstanceID() ?? 0;
                parts.Add($"{quest.GetInstanceID()}:{(int)entry.Source}:{(int)quest.GetKind()}:" +
                          $"{quest.questGoal}:{actionable}:{enemyId}:{enemyTypeId}:" +
                          $"{quest.map?.GetInstanceID() ?? 0}:{quest.mapEvent?.GetInstanceID() ?? 0}:{quest.eventSet}");
            }
            catch
            {
            // One stale quest during a scene transition must not block other quests from the signature.
            }
        }
        parts.Sort(StringComparer.Ordinal);
        var signature = string.Join("|", parts);
        if (string.Equals(signature, _snapshotPlanSignature, StringComparison.Ordinal)) return;
        _snapshotPlanSignature = signature;
        _snapshotPlanRevision++;
        _nextPortfolioScan = 0f;
        _nextPrimaryPreemptionScan = 0f;
        InvalidatePlanCaches();
    }

    private static string MapCacheKey(BaseMap map)
    {
        if (map == null) return string.Empty;
        try { return map.id ?? map.name ?? map.GetInstanceID().ToString(); }
        catch { return string.Empty; }
    }

    private void RefreshSnapshot()
    {
        _snapshot.Clear();
        var ids = new HashSet<int>();

        // Daily and Weekly UI caches retain stale objects after generation or reroll. Read only active ScriptableObjects.
        try
        {
            foreach (var daily in Resources.FindObjectsOfTypeAll<DailyQuest>())
                if (IsActiveGeneratedQuest(daily)) AddQuest(daily, QuestSource.Daily, ids);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Daily quest snapshot failed; regeneration is suspended.", ex);
        }

        try
        {
            foreach (var weekly in Resources.FindObjectsOfTypeAll<WeeklyQuest>())
                if (IsActiveGeneratedQuest(weekly)) AddQuest(weekly, QuestSource.Weekly, ids);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Weekly quest snapshot failed; regeneration is suspended.", ex);
        }

        try
        {
            foreach (var quest in Resources.FindObjectsOfTypeAll<Quest>())
            {
                if (quest == null || quest.TryCast<DailyQuest>() != null || quest.TryCast<WeeklyQuest>() != null)
                    continue;
                if (!QuestApi.IsSupported(quest) || !quest.CanBeCompletedOrClaimed()) continue;
                AddQuest(quest, quest.mapEvent != null ? QuestSource.Event : QuestSource.Normal, ids);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Normal quest snapshot failed; quest automation is suspended.", ex);
        }
        ObserveRuntimeProgress();
        UpdateSnapshotPlanRevision();
        LogUnavailableZyniumQuest();
        _unavailableDailyCount = _snapshot.Count(x => x.Source == QuestSource.Daily && !RequirementsAvailable(x));
        _unavailableWeeklyCount = _snapshot.Count(x => x.Source == QuestSource.Weekly && !RequirementsAvailable(x));
        if (_unavailableDailyCount != _lastLoggedUnavailableDaily ||
            _unavailableWeeklyCount != _lastLoggedUnavailableWeekly)
        {
            _lastLoggedUnavailableDaily = _unavailableDailyCount;
            _lastLoggedUnavailableWeekly = _unavailableWeeklyCount;
            if (_unavailableDailyCount + _unavailableWeeklyCount > 0)
                SmartLog.Warning(SmartLogCategory.Quest,
                    $"Quest validity guard skipped unavailable objects: Daily={_unavailableDailyCount}, Weekly={_unavailableWeeklyCount}.");
        }
    }

    private void ObserveRuntimeProgress()
    {
        if (!Plugin.Config.QuestRuntimeCalibrationEnabled.Value) return;
        var now = Time.unscaledTime;
        var active = new HashSet<int>();
        foreach (var entry in _snapshot)
        {
            var quest = entry?.Quest;
            if (quest == null || quest.isClaimed) continue;
            int id;
            try { id = quest.GetInstanceID(); }
            catch { continue; }
            active.Add(id);
            var type = quest.GetKind();
            var progress = Math.Max(0, quest.questCurrentGoal);
            if (!_progressObservations.TryGetValue(id, out var observation) || observation.Type != type ||
                progress < observation.Progress)
            {
                _progressObservations[id] = new QuestProgressObservation(type, progress, now);
                continue;
            }
            if (progress <= observation.Progress) continue;

            var elapsed = now - observation.ProgressAt;
            var delta = progress - observation.Progress;
            if (elapsed >= 0.2f && elapsed <= 1800f && delta > 0)
            {
                var secondsPerProgress = elapsed / delta;
                if (double.IsFinite(secondsPerProgress) && secondsPerProgress > 0 &&
                    !QuestApi.UsesUnboostedProgress(quest))
                {
                    if (!_runtimeCalibrations.TryGetValue(type, out var calibration))
                    {
                        calibration = new QuestRuntimeCalibration();
                        _runtimeCalibrations[type] = calibration;
                    }
                    calibration.Observe(secondsPerProgress);
                    _runtimeCalibrationDirty = true;
                    ObserveEnemyPatternRate(entry, delta, elapsed);
                }
            }
            observation.Progress = progress;
            observation.ProgressAt = now;
        }

        foreach (var id in _progressObservations.Keys.Where(id => !active.Contains(id)).ToArray())
        {
            _progressObservations.Remove(id);
            _stalledDailyProtectionLogged.Remove(id);
            _keptDailyRageOpportunityIds.Remove(id);
            _keptDailyPassiveCollectableIds.Remove(id);
            _keptDailyParallelMaterialIds.Remove(id);
            _keptDailyCraftableIds.Remove(id);
        }
        if (_runtimeCalibrationDirty && now >= _nextRuntimeCalibrationPersistAt)
            SaveRuntimeCalibrations(now);
    }

    private void LoadRuntimeCalibrations()
    {
        if (!Plugin.Config.QuestRuntimeCalibrationEnabled.Value) return;
        var data = Plugin.Config.QuestRuntimeCalibrationData.Value;
        if (string.IsNullOrWhiteSpace(data)) return;
        foreach (var token in data.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split(',');
            if (parts.Length != 3 || !Enum.TryParse<QuestKind>(parts[0], true, out var type) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var samples) ||
                !double.IsFinite(seconds) || seconds <= 0 || samples <= 0) continue;
            _runtimeCalibrations[type] = new QuestRuntimeCalibration(seconds, samples);
        }
    }

    private static void ObserveEnemyPatternRate(QuestEntry entry, int progressDelta, float elapsed)
    {
        var quest = entry?.Quest;
        if (quest == null || quest.GetKind() != QuestKind.KillEnemies || !IsUnrestrictedKillObjective(quest) ||
            QuestApi.UsesUnboostedProgress(quest) || progressDelta <= 0 || elapsed < 0.2f) return;
        try
        {
            var map = MapController.instance?.selectedMap;
            if (!IsStandardAvailableMap(map)) return;
            var estimate = MapEfficiencyEstimator.EvaluateForQuest(map, quest);
            var density = MapEfficiencyEstimator.EffectiveDensity(estimate);
            if (density <= 0) return;
            var liveCredit = Math.Max(1, PlayerInventory.instance?.questProgressPerKill ?? 1);
            var patternsPerMinute = progressDelta / (double)liveCredit / density / elapsed * 60.0;
            if (!double.IsFinite(patternsPerMinute) || patternsPerMinute < 1 || patternsPerMinute > 120) return;
            var count = Math.Max(0, Plugin.Config.EnemyPatternRateSampleCount.Value);
            var current = Math.Clamp(Plugin.Config.ExpectedEnemyPatternsPerMinute.Value, 1, 120);
            var alpha = count < 5 ? 1.0 / (count + 1) : 0.15;
            Plugin.Config.ExpectedEnemyPatternsPerMinute.Value =
                (float)Math.Clamp(current + alpha * (patternsPerMinute - current), 1, 120);
            Plugin.Config.EnemyPatternRateSampleCount.Value = Math.Min(1000000, count + 1);
        }
            catch { /* Ignore a sample during scene transition or quest invalidation. */ }
    }

    private void SaveRuntimeCalibrations(float now)
    {
        Plugin.Config.QuestRuntimeCalibrationData.Value = string.Join(";", _runtimeCalibrations
            .Where(x => x.Value.Samples > 0 && double.IsFinite(x.Value.SecondsPerProgress))
            .OrderBy(x => x.Key.ToString())
            .Select(x => $"{x.Key},{x.Value.SecondsPerProgress.ToString("R", CultureInfo.InvariantCulture)}," +
                         $"{x.Value.Samples.ToString(CultureInfo.InvariantCulture)}"));
        _runtimeCalibrationDirty = false;
        _nextRuntimeCalibrationPersistAt = now + 30f;
    }

    internal string RuntimeCalibrationSummary()
    {
        if (_runtimeCalibrations.Count == 0) return "Runtime calibration: no valid samples";
        return "Runtime calibration: " + string.Join("; ", _runtimeCalibrations
            .Where(x => x.Value.Samples > 0)
            .OrderByDescending(x => x.Value.Samples)
            .Take(8)
            .Select(x => $"{x.Key}={x.Value.SecondsPerProgress:0.###}s/progress ({x.Value.Samples} samples)"));
    }

    private void RecordCompletedDailyDuration(Quest quest)
    {
        if (!Plugin.Config.AdaptiveDailyRerollThresholdEnabled.Value || quest == null) return;
        QuestProgressObservation observation;
        try
        {
            if (!_progressObservations.TryGetValue(quest.GetInstanceID(), out observation)) return;
        }
        catch { return; }
        var goal = Math.Max(1, quest.questGoal);
        var observedProgress = Math.Max(0, goal - observation.InitialProgress);
        var coverage = Math.Clamp(observedProgress / (double)goal, 0, 1);
        // When starting mid-quest with less than half observed, linear scaling would treat one special window as total duration.
        if (coverage < 0.5) return;
        var elapsed = observation.ActiveElapsedSeconds;
        if (elapsed < 0.5f || elapsed > 3600f) return;
        var normalizedSeconds = Math.Clamp(elapsed / coverage, 1, 3600);

        var count = Math.Max(0, Plugin.Config.DailyCompletionSampleCount.Value);
        var oldMean = Math.Max(0, Plugin.Config.DailyCompletionMeanSeconds.Value);
        var oldDeviation = Math.Max(0, Plugin.Config.DailyCompletionDeviationSeconds.Value);
        var acceptedSeconds = normalizedSeconds;
        // Opportunity windows differ greatly by quest type. One Giant or minigame pause must not raise every Daily
        // threshold from one minute to many; winsorize mature-model samples while allowing gradual real speed changes.
        if (count >= 5 && oldMean > 0)
            acceptedSeconds = Math.Min(normalizedSeconds, Math.Max(60, oldMean * 3));
        double mean;
        double deviation;
        if (count == 0 || oldMean <= 0)
        {
            mean = acceptedSeconds;
            deviation = 0;
        }
        else
        {
            var alpha = count < 5 ? 1.0 / (count + 1) : count < 30 ? 0.12 : 0.08;
            mean = oldMean + alpha * (acceptedSeconds - oldMean);
            deviation = oldDeviation + alpha * (Math.Abs(acceptedSeconds - mean) - oldDeviation);
        }
        Plugin.Config.DailyCompletionMeanSeconds.Value = (float)mean;
        Plugin.Config.DailyCompletionDeviationSeconds.Value = (float)Math.Max(0, deviation);
        Plugin.Config.DailyCompletionSampleCount.Value = Math.Min(1000000, count + 1);
        var clipped = acceptedSeconds + 0.5 < normalizedSeconds
            ? $" (slow outlier accepted as {FormatDuration(acceptedSeconds)})"
            : string.Empty;
        SmartLog.Debug(SmartLogCategory.Quest,
            $"Recorded Daily completion sample: type={quest.GetKind()}, normalized={FormatDuration(normalizedSeconds)}{clipped}, " +
            $"current reroll threshold={FormatDuration(DailyRerollThresholdSeconds())}.");
    }

    private bool HasAdaptiveDailyRerollThreshold =>
        Plugin.Config.AdaptiveDailyRerollThresholdEnabled.Value &&
        Plugin.Config.DailyCompletionSampleCount.Value >= 3 &&
        Plugin.Config.DailyCompletionMeanSeconds.Value > 0;

    private double DailyRerollThresholdSeconds()
    {
        if (!HasAdaptiveDailyRerollThreshold)
            return Math.Max(30, Plugin.Config.DailyRerollExpectedSeconds.Value);
        var mean = Math.Max(1, Plugin.Config.DailyCompletionMeanSeconds.Value);
        var deviation = Math.Max(0, Plugin.Config.DailyCompletionDeviationSeconds.Value);
        var rerollCost = Math.Max(5, Plugin.Config.MinimumRerollIntervalSeconds.Value);
        var robustDeviation = Math.Min(deviation, Math.Max(30, mean));
        // Cold-start configuration is a robust ceiling only; normal behavior is driven by historical mean and variance.
        // This immediately recovers data polluted by one 30-minute legacy sample without waiting for dozens of new quests.
        var safetyCeiling = Math.Max(60, Plugin.Config.DailyRerollExpectedSeconds.Value);
        return Math.Clamp(mean + robustDeviation + rerollCost, 30, safetyCeiling);
    }

    internal string DailyRerollThresholdSummary()
    {
        var count = Math.Max(0, Plugin.Config.DailyCompletionSampleCount.Value);
        if (!HasAdaptiveDailyRerollThreshold)
            return $"Daily reroll threshold: cold start={DailyRerollThresholdSeconds():0}s, samples={count}/3";
        return $"Daily reroll threshold: adaptive={DailyRerollThresholdSeconds():0}s, " +
               $"mean={Plugin.Config.DailyCompletionMeanSeconds.Value:0}s, " +
               $"deviation={Plugin.Config.DailyCompletionDeviationSeconds.Value:0}s, " +
               $"robust ceiling={Math.Max(60, Plugin.Config.DailyRerollExpectedSeconds.Value):0}s, samples={count}";
    }

    private static bool IsActiveGeneratedQuest(Quest quest)
    {
        if (quest == null) return false;
        try
        {
            return quest switch
            {
                DailyQuest daily => daily.active && !daily.isClaimed,
                WeeklyQuest weekly => weekly.active && !weekly.isClaimed,
                _ => false
            };
        }
        catch { return false; }
    }

    private void AddQuest(Quest quest, QuestSource source, HashSet<int> ids)
    {
        // Keep unknown active Daily/Weekly entries in the snapshot so they are not mistaken for
        // empty slots and regenerated. Action eligibility is checked independently.
        if (quest == null || quest.isClaimed) return;
        var id = quest.GetInstanceID();
        if (!ids.Add(id)) return;
        _snapshot.Add(new QuestEntry(quest, source));
    }

    private void WaitForQuestState(float seconds, int removedQuestId = 0)
    {
        _questStateSettleUntil = Time.unscaledTime + Math.Max(0.5f, seconds);
        _nextScan = _questStateSettleUntil;
        _nextPortfolioScan = 0f;
        _portfolio = null;
        if (removedQuestId != 0)
            _snapshot.RemoveAll(entry => entry?.Quest != null &&
                                         entry.Quest.GetInstanceID() == removedQuestId);
        _snapshotPlanRevision++;
        InvalidatePlanCaches();
    }

    private bool TryClaimOne()
    {
        foreach (var entry in _snapshot)
        {
            var quest = entry.Quest;
            if (quest == null || quest.isClaimed || !QuestApi.IsSupported(quest)) continue;
            try
            {
                // CanBeClaimed checks only progress and claimed state. EventAvailable additionally
                // checks the live event window and the native EventQuestSet.Unlocked requirement.
                if (!QuestApi.EventAvailable(quest) || !quest.CanBeClaimed()) continue;
                var claimedType = quest.GetKind();
                quest.Claim();
                if (entry.Source == QuestSource.Daily) RecordCompletedDailyDuration(quest);
                SmartLog.Info(SmartLogCategory.Quest,
                    $"Claimed completed quest; type={claimedType}, instanceId={quest.GetInstanceID()}.");
                if (entry.Source == QuestSource.Daily) _dailyRerollChain = 0;
                if (entry.Source == QuestSource.Weekly)
                {
                    _weeklyRerollChain = 0;
                    AdvanceWeeklyTargetAfterClaim(claimedType);
                }
                SchedulePostClaimReview(entry.Source);
                if (quest.GetInstanceID() == _lockedQuestId) _lockedQuestId = 0;
                WaitForQuestState(1.5f, quest.GetInstanceID());
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    // Some Weeklies update authoritative data before a destroyed UI object throws a trailing exception.
                    if (quest != null && quest.isClaimed)
                    {
                        var claimedType = quest.GetKind();
                        if (entry.Source == QuestSource.Daily) RecordCompletedDailyDuration(quest);
                        SmartLog.Info(SmartLogCategory.Quest,
                            $"Claimed completed quest after authoritative state updated despite a trailing UI exception; " +
                            $"type={claimedType}, instanceId={quest.GetInstanceID()}.");
                        if (entry.Source == QuestSource.Daily) _dailyRerollChain = 0;
                        if (entry.Source == QuestSource.Weekly)
                        {
                            _weeklyRerollChain = 0;
                            AdvanceWeeklyTargetAfterClaim(claimedType);
                        }
                        SchedulePostClaimReview(entry.Source);
                        if (quest.GetInstanceID() == _lockedQuestId) _lockedQuestId = 0;
                        WaitForQuestState(1.5f, quest.GetInstanceID());
                        return true;
                    }
                }
                catch { /* Preserve the original error when authoritative state cannot be confirmed. */ }
                SmartLog.Warning(SmartLogCategory.Quest,
                    $"Unable to claim quest type={quest?.GetKind()}; skipping this cycle: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return false;
    }

    private void SchedulePostClaimReview(QuestSource source)
    {
        var reviewAt = Time.unscaledTime + RuntimeCadence.QuestMutationSettleSeconds;
        if (source == QuestSource.Daily)
        {
            _dailyPostClaimReviewAt = reviewAt;
            _dailyRerollsSinceClaim = 0;
            _dailyReplacementBaseline = null;
            _dailyReplacementOriginalId = 0;
            _dailyReplacementDeadline = 0f;
        }
        if (source == QuestSource.Weekly)
        {
            _weeklyPostClaimReviewAt = reviewAt;
            _weeklyReplacementBaseline = null;
            _weeklyReplacementOriginalId = 0;
            _weeklyReplacementDeadline = 0f;
        }
    }

    private bool TryRegenerateMissing()
    {
        var hasDaily = _snapshot.Any(x => x.Source == QuestSource.Daily && x.Quest != null && !x.Quest.isClaimed);
        var hasWeekly = _snapshot.Any(x => x.Source == QuestSource.Weekly && x.Quest != null && !x.Quest.isClaimed);
        _dailyMissingScans = hasDaily ? 0 : _dailyMissingScans + 1;
        _weeklyMissingScans = hasWeekly ? 0 : _weeklyMissingScans + 1;
        var now = Time.unscaledTime;
        try
        {
        // Require two consecutive empty scans before generation so scene initialization or a transient list refresh
        // cannot create duplicate sets.
            if (!hasDaily && _dailyMissingScans >= 2 && now >= _nextDailyRegenerateAt &&
                Plugin.Config.AutoRegenerateDailyQuests.Value && CraftableItems.list?.DailyQuests != null && CraftableItems.list.DailyQuests.IsActive())
            {
                var manager = DailyQuestsManager.instance;
                if (manager != null)
                {
                    manager.RegenerateDailys();
                    UpdateGeneratedRerollCandidates(now);
                    _nextDailyRegenerateAt = now + 20f;
                    _dailyMissingScans = 0;
                    _dailyRerollChain = 0;
                    SmartLog.Info(SmartLogCategory.Quest,
                        "No active Daily quests remained; requested a new Daily set.");
                    WaitForQuestState(5f);
                    return true;
                }
            }
            if (!hasWeekly && _weeklyMissingScans >= 2 && now >= _nextWeeklyRegenerateAt &&
                Plugin.Config.AutoRegenerateWeeklyQuests.Value && CraftableItems.list?.WeeklyQuests != null && CraftableItems.list.WeeklyQuests.IsActive())
            {
                var manager = WeeklyQuestsManager.instance;
                if (manager != null)
                {
                    manager.RegenerateWeeklies();
                    UpdateGeneratedRerollCandidates(now);
                    _nextWeeklyRegenerateAt = now + 20f;
                    _weeklyMissingScans = 0;
                    _weeklyRerollChain = 0;
                    SmartLog.Info(SmartLogCategory.Quest,
                        "No active Weekly quests remained; requested a new Weekly set.");
                    WaitForQuestState(5f);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _nextMutation = Time.unscaledTime + RuntimeCadence.QuestGenerationFailureRetrySeconds;
            SmartLog.Warning(SmartLogCategory.Quest,
                $"Unable to generate Daily or Weekly quests; retrying in {RuntimeCadence.QuestGenerationFailureRetrySeconds:0.#}s: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
        return false;
    }

    private bool TryRerollOne()
    {
        var weeklyTargetStrategy = Plugin.Config.WeeklyPreferredPairStrategy.Value;
        var preferredWeekly = weeklyTargetStrategy
            ? _snapshot.Where(x => x.Source == QuestSource.Weekly && x.Quest != null &&
                                   !x.Quest.isClaimed && IsCurrentWeeklyTarget(x.Quest.GetKind())).ToArray()
            : Array.Empty<QuestEntry>();
        if (preferredWeekly.Length > 0)
        {
            if (!_weeklyPreferredTargetLogged)
            {
                _weeklyPreferredTargetLogged = true;
                var types = string.Join(" + ", preferredWeekly.Select(x => x.Quest.GetKind()).Distinct());
                SmartLog.Info(SmartLogCategory.Quest,
                    $"Preserved the current Weekly phase target ({types}); other slots in this set will not reroll until it is claimed.");
            }
        }
        else
        {
            _weeklyPreferredTargetLogged = false;
        }

        QuestEntry worst = null;
        var worstCostRatio = -1.0;
        foreach (var entry in _snapshot)
        {
            if (entry.Source is not (QuestSource.Daily or QuestSource.Weekly) || !IsActionable(entry)) continue;
            var quest = entry.Quest;
            var id = quest.GetInstanceID();
            if (entry.Source == QuestSource.Daily && IsPassiveCollectableDailyType(quest.GetKind()))
            {
            // Coins and map-agnostic gemstones complete naturally during kill, material, and other quest routes, so their
            // marginal route time is zero. Comparing isolated collection history with the global threshold would turn
            // a real 6-10 second coin quest into a false two-minute estimate.
                RemoveGeneratedRerollCandidate(entry.Source, id);
                if (_keptDailyPassiveCollectableIds.Add(id))
                    SmartLog.Debug(SmartLogCategory.Quest,
                        $"Keeping passive Daily quest {quest.GetKind()} at {quest.questCurrentGoal}/{quest.questGoal}; it progresses alongside the current route.");
                continue;
            }
            if (entry.Source == QuestSource.Weekly && weeklyTargetStrategy &&
                (IsCurrentWeeklyTarget(quest.GetKind()) || preferredWeekly.Length > 0))
            {
            // Freeze the whole set once the current phase target appears. Rerolling another slot into the next phase early
            // would let Portal quests preempt permanently at the start of every set.
                RemoveGeneratedRerollCandidate(entry.Source, id);
                continue;
            }
            var generatedCandidate = entry.Source == QuestSource.Daily
                ? _dailyGeneratedRerollCandidates.Contains(id) && Time.unscaledTime >= _dailyGeneratedReadyAt
                : _weeklyGeneratedRerollCandidates.Contains(id) && Time.unscaledTime >= _weeklyGeneratedReadyAt;
            // Explicitly undesirable automation types cannot remain forever after a reload or legacy candidate removal.
            // They still honor progress protection, native eligibility, intervals, and session safety limits.
            var stalledProtectedDaily = entry.Source == QuestSource.Daily &&
                                        IsStalledDailyEventQuest(quest);
            var forcedExistingDailyCandidate = entry.Source == QuestSource.Daily &&
                                               (ShouldForceReroll(quest.GetKind(), QuestSource.Daily) ||
                                                stalledProtectedDaily ||
                                                quest.GetKind() == QuestKind.KillEnemiesWithRageMode);
            if (!generatedCandidate && !forcedExistingDailyCandidate) continue;
            var seekingWeeklyTarget = entry.Source == QuestSource.Weekly && weeklyTargetStrategy &&
                                       preferredWeekly.Length == 0;
            var target = ResolveTargetMap(quest, out var density);
            var noStandardEnemyTarget = NeedsEnemyMap(quest.GetKind()) && (target == null || density <= 0);
            if (entry.Source == QuestSource.Daily && quest.GetKind() == QuestKind.GetMaterials &&
                target != null && density > 0 && !stalledProtectedDaily)
            {
                // Material quests progress on the same kills used by normal quests, staff supply and
                // idle surplus recovery.  Capacity relief and Whetstone are already automated, so
                // their marginal route cost is zero; comparing a standalone estimate to the global
                // reroll line caused the last slot to search forever while we were already farming.
                RemoveGeneratedRerollCandidate(entry.Source, id);
                if (_keptDailyParallelMaterialIds.Add(id))
                    SmartLog.Debug(SmartLogCategory.Quest,
                        $"Keeping parallel material Daily at {quest.questCurrentGoal}/{quest.questGoal}; map={target.name}, shared with quest and supply kills.");
                continue;
            }
            if (entry.Source == QuestSource.Daily && quest.GetKind() == QuestKind.CraftTemporaryItems)
            {
                var remainingCrafts = Math.Max(0, quest.questGoal - quest.questCurrentGoal);
                if (StrategyBehaviour.CanCompleteCraftQuestWithDragonEggs(remainingCrafts))
                {
                    RemoveGeneratedRerollCandidate(entry.Source, id);
                    if (_keptDailyCraftableIds.Add(id))
                        SmartLog.Debug(SmartLogCategory.Quest,
                            $"Keeping craftable Daily: remaining={remainingCrafts:0}; Dragon Eggs above reserve can finish it exactly.");
                    continue;
                }
            }
            if (noStandardEnemyTarget && CurrentNonStandardMapContainsTarget(quest))
            {
                // Bonus-Stage-only targets such as Goblin and Goblin King use the active stage window first while remaining
                // candidates. If incomplete after exit, the next cycle forces a reroll.
                continue;
            }
            if (entry.Source == QuestSource.Daily &&
                IsPreferredReachableKillQuest(quest, target, density))
            {
                RemoveGeneratedRerollCandidate(entry.Source, id);
                if (_keptDailyReachableKillIds.Add(id))
                    SmartLog.Debug(SmartLogCategory.Quest,
                        $"Keeping reachable Daily kill quest {quest.GetKind()} at {quest.questCurrentGoal}/{quest.questGoal}; map={target.name}.");
                continue;
            }
            var ratio = quest.questGoal <= 0 ? 0 : quest.questCurrentGoal * 100.0 / quest.questGoal;
            if (!seekingWeeklyTarget && !noStandardEnemyTarget &&
                ratio >= Math.Clamp(Plugin.Config.KeepRerollProgressPercent.Value, 0, 100))
            {
                if (!stalledProtectedDaily)
                {
            // Event quests remain candidates for reevaluation after stalling; other quests retain hard progress protection
            // to avoid wasting valuable kill progress.
                    if (entry.Source != QuestSource.Daily || !IsStallSensitiveDailyType(quest.GetKind()))
                        RemoveGeneratedRerollCandidate(entry.Source, id);
                    continue;
                }
                if (_stalledDailyProtectionLogged.Add(id))
                    SmartLog.Debug(SmartLogCategory.Quest,
                        $"Released Daily progress protection after a stall: type={quest.GetKind()}, progress={quest.questCurrentGoal}/{quest.questGoal}, " +
                        $"stall={Math.Max(20f, Plugin.Config.QuestStallSeconds.Value):0}s.");
            }
            var expectedSeconds = EstimateSeconds(quest, entry.Source, target, density);
            double thresholdSeconds = entry.Source == QuestSource.Daily
                ? DailyRerollThresholdSeconds()
                : Math.Max(60, Plugin.Config.WeeklyRerollExpectedMinutes.Value * 60);
            var legacyScale = entry.Source == QuestSource.Daily
                ? Plugin.Config.DailyRerollDifficultyThreshold.Value
                : Plugin.Config.WeeklyRerollDifficultyThreshold.Value;
            thresholdSeconds *= Math.Clamp(legacyScale, 1, 100) / 45.0;
            var dailyRageQuest = entry.Source == QuestSource.Daily &&
                                 quest.GetKind() == QuestKind.KillEnemiesWithRageMode;
            var rageKeepReason = string.Empty;
            var rerollDailyRageForCost = dailyRageQuest &&
                                         ShouldRerollDailyRageQuest(quest, density, out rageKeepReason);
            if (dailyRageQuest && !rerollDailyRageForCost && _keptDailyRageOpportunityIds.Add(id))
                SmartLog.Debug(SmartLogCategory.Quest,
                    $"Keeping Daily Rage-kill opportunity at {quest.questCurrentGoal}/{quest.questGoal}; only natural Rage and active bonuses will be used.");
            var exceedsAdaptiveDailyThreshold = entry.Source == QuestSource.Daily && !dailyRageQuest &&
                                                expectedSeconds >= thresholdSeconds;
            var force = seekingWeeklyTarget || noStandardEnemyTarget || stalledProtectedDaily ||
                        rerollDailyRageForCost ||
                        exceedsAdaptiveDailyThreshold ||
                        ShouldForceReroll(quest.GetKind(), entry.Source);
            if (!force && expectedSeconds < thresholdSeconds)
            {
                if (!dailyRageQuest) RemoveGeneratedRerollCandidate(entry.Source, id);
                continue;
            }
            var costRatio = seekingWeeklyTarget
                ? 1000000 + expectedSeconds / thresholdSeconds
                : force ? 1000 + expectedSeconds / thresholdSeconds : expectedSeconds / thresholdSeconds;
            if (costRatio <= worstCostRatio) continue;
            if (_rerollFailures.TryGetValue(id, out var failures) && failures >= 3)
            {
                RemoveGeneratedRerollCandidate(entry.Source, id);
                continue;
            }
            if (entry.Source == QuestSource.Daily && (!Plugin.Config.AutoRerollDailyQuests.Value ||
                _dailyRerollsSinceClaim >= Math.Max(0, Plugin.Config.MaximumDailyRerollsPerSession.Value) ||
                Time.unscaledTime < _nextDailyRerollAt)) continue;
            if (entry.Source == QuestSource.Weekly && (!Plugin.Config.AutoRerollWeeklyQuests.Value ||
                _weeklyRerolls >= Math.Max(0, seekingWeeklyTarget
                    ? Plugin.Config.WeeklyPreferredPairRerollLimit.Value
                    : Plugin.Config.MaximumWeeklyRerollsPerSession.Value) ||
                (!force && _weeklyRerollChain >= Math.Max(0, Plugin.Config.MaximumConsecutiveWeeklyRerolls.Value)) ||
                Time.unscaledTime < _nextWeeklyRerollAt)) continue;
            worst = entry;
            worstCostRatio = costRatio;
        }
        if (worst == null) return false;
        return worst.Source == QuestSource.Daily ? RerollDaily(worst.Quest) : RerollWeekly(worst.Quest);
    }

    private void RemoveGeneratedRerollCandidate(QuestSource source, int id)
    {
        if (source == QuestSource.Daily) _dailyGeneratedRerollCandidates.Remove(id);
        if (source == QuestSource.Weekly) _weeklyGeneratedRerollCandidates.Remove(id);
    }

    private bool RerollDaily(Quest quest)
    {
        var daily = FindDailyById(quest.GetInstanceID());
        var reroll = DailyQuestReroll.instance;
        if (daily == null || reroll == null) return false;
        try
        {
            if (!reroll.rerollEnabled)
            {
                LogRerollPermissionWait("Daily");
                _nextDailyRerollAt = Time.unscaledTime + RuntimeCadence.QuestRerollAvailabilityRetrySeconds;
                return false;
            }
        }
        catch
        {
            _nextDailyRerollAt = Time.unscaledTime + RuntimeCadence.QuestRerollAvailabilityRetrySeconds;
            return false;
        }
        var activeBefore = QuestGenerationBridge.ActiveDailyIds();
        var expected = EstimateSeconds(quest, QuestSource.Daily, ResolveTargetMap(quest, out var density), density);
        try
        {
            reroll.PrepareReroll(daily);
            if (reroll.dailyQuestToReroll == null || reroll.dailyQuestToReroll.GetInstanceID() != daily.GetInstanceID())
                throw new InvalidOperationException("The Daily reroll controller did not bind the selected quest.");
            reroll.RewardForShowing();
            return CompleteDailyReroll(quest, expected, false, activeBefore);
        }
        catch (Exception ex)
        {
            if (BecameInactive(daily))
            {
                SmartLog.Debug(SmartLogCategory.Quest,
                    $"Daily reroll replaced authoritative state before a trailing UI exception: type={quest.GetKind()}, exception={ex.GetType().Name}.");
                return CompleteDailyReroll(quest, expected, true, activeBefore);
            }
            CountFailure(quest);
            SmartLog.Warning(SmartLogCategory.Quest,
                $"Daily reroll failed for type={quest.GetKind()}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool RerollWeekly(Quest quest)
    {
        var weekly = FindWeeklyById(quest.GetInstanceID());
        var reroll = WeeklyQuestReroll.instance;
        if (weekly == null || reroll == null) return false;
        try
        {
            if (!reroll.rerollEnabled)
            {
                LogRerollPermissionWait("Weekly");
                _nextWeeklyRerollAt = Time.unscaledTime + RuntimeCadence.QuestRerollAvailabilityRetrySeconds;
                return false;
            }
        }
        catch
        {
            _nextWeeklyRerollAt = Time.unscaledTime + RuntimeCadence.QuestRerollAvailabilityRetrySeconds;
            return false;
        }
        var activeBefore = QuestGenerationBridge.ActiveWeeklyIds();
        var expected = EstimateSeconds(quest, QuestSource.Weekly, ResolveTargetMap(quest, out var density), density);
        try
        {
            reroll.PrepareReroll(weekly);
            if (reroll.weeklyQuestToReroll == null || reroll.weeklyQuestToReroll.GetInstanceID() != weekly.GetInstanceID())
                throw new InvalidOperationException("The Weekly reroll controller did not bind the selected quest.");
            reroll.RewardForShowing();
            return CompleteWeeklyReroll(quest, expected, false, activeBefore);
        }
        catch (Exception ex)
        {
            if (BecameInactive(weekly))
            {
                SmartLog.Debug(SmartLogCategory.Quest,
                    $"Weekly reroll replaced authoritative state before a trailing UI exception: type={quest.GetKind()}, exception={ex.GetType().Name}.");
                return CompleteWeeklyReroll(quest, expected, true, activeBefore);
            }
            CountFailure(quest);
            SmartLog.Warning(SmartLogCategory.Quest,
                $"Weekly reroll failed for type={quest.GetKind()}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool CompleteDailyReroll(Quest quest, double expected, bool recoveredFromUiException,
        HashSet<int> activeBefore)
    {
        _dailyRerolls++;
        _dailyRerollsSinceClaim++;
        _dailyRerollChain++;
        _nextDailyRerollAt = Time.unscaledTime + Math.Max(5f, Plugin.Config.MinimumRerollIntervalSeconds.Value);
        var rerolledQuestId = quest.GetInstanceID();
        // Background reroll replaces only the current slot. Preserve another active kill or persistent quest lock;
        // otherwise a slow Daily rerolled every five seconds briefly replans the primary route and opens a reverse Portal.
        if (rerolledQuestId == _lockedQuestId) _lockedQuestId = 0;
        _dailyGeneratedRerollCandidates.Remove(rerolledQuestId);
        _dailyReplacementBaseline = activeBefore;
        _dailyReplacementOriginalId = quest.GetInstanceID();
        _dailyReplacementDeadline = Time.unscaledTime + RuntimeCadence.QuestReplacementSettleTimeoutSeconds;
        SmartLog.Info(SmartLogCategory.Quest,
            $"Rerolled Daily quest: type={quest.GetKind()}, instanceId={quest.GetInstanceID()}, progress={quest.questCurrentGoal}/{quest.questGoal}, " +
            $"expected={FormatDuration(expected)}, threshold={FormatDuration(DailyRerollThresholdSeconds())}, chain={_dailyRerollChain}, " +
            $"since claim={_dailyRerollsSinceClaim}/{Math.Max(0, Plugin.Config.MaximumDailyRerollsPerSession.Value)}, session={_dailyRerolls}, " +
            $"recovered after UI exception={recoveredFromUiException}.");
        WaitForQuestState(5f, quest.GetInstanceID());
        return true;
    }

    private bool CompleteWeeklyReroll(Quest quest, double expected, bool recoveredFromUiException,
        HashSet<int> activeBefore)
    {
        _weeklyRerolls++;
        _weeklyRerollChain++;
        _nextWeeklyRerollAt = Time.unscaledTime + Math.Max(5f, Plugin.Config.MinimumRerollIntervalSeconds.Value);
        var rerolledQuestId = quest.GetInstanceID();
        if (rerolledQuestId == _lockedQuestId) _lockedQuestId = 0;
        _weeklyGeneratedRerollCandidates.Remove(rerolledQuestId);
        _weeklyReplacementBaseline = activeBefore;
        _weeklyReplacementOriginalId = quest.GetInstanceID();
        _weeklyReplacementDeadline = Time.unscaledTime + RuntimeCadence.QuestReplacementSettleTimeoutSeconds;
        var chainCap = Math.Max(0, Plugin.Config.MaximumConsecutiveWeeklyRerolls.Value);
        var seekingTarget = Plugin.Config.WeeklyPreferredPairStrategy.Value &&
            !IsCurrentWeeklyTarget(quest.GetKind());
        var forcedPastChainCap = _weeklyRerollChain > chainCap &&
            (seekingTarget || ShouldForceReroll(quest.GetKind(), QuestSource.Weekly));
        var sessionCap = seekingTarget
            ? Math.Max(0, Plugin.Config.WeeklyPreferredPairRerollLimit.Value)
            : Math.Max(0, Plugin.Config.MaximumWeeklyRerollsPerSession.Value);
        SmartLog.Info(SmartLogCategory.Quest,
            $"Rerolled Weekly quest: type={quest.GetKind()}, instanceId={quest.GetInstanceID()}, progress={quest.questCurrentGoal}/{quest.questGoal}, " +
            $"expected={FormatDuration(expected)}, chain={_weeklyRerollChain}/{chainCap}, seeking target={seekingTarget}, target={_weeklyPreferredTarget}, " +
            $"forced past chain cap={forcedPastChainCap}, session={_weeklyRerolls}/{sessionCap}, recovered after UI exception={recoveredFromUiException}.");
        WaitForQuestState(5f, quest.GetInstanceID());
        return true;
    }

    private void LogRerollPermissionWait(string label)
    {
        if (Time.unscaledTime < _nextRerollPermissionLog) return;
        _nextRerollPermissionLog = Time.unscaledTime + RuntimeCadence.QuestRerollPermissionLogSeconds;
        SmartLog.Debug(SmartLogCategory.Quest,
            "A quest was eligible for reroll, but the native reroll object was not ready; keeping the quest until eligibility is restored.");
    }

    private static bool BecameInactive(Quest quest)
    {
        if (quest == null) return false;
        try
        {
            return quest switch
            {
                DailyQuest daily => !daily.active,
                WeeklyQuest weekly => !weekly.active,
                _ => false
            };
        }
        catch { return false; }
    }

    private static DailyQuest FindDailyById(int id)
    {
        foreach (var quest in Resources.FindObjectsOfTypeAll<DailyQuest>())
            if (quest != null && quest.GetInstanceID() == id) return quest;
        return null;
    }

    private static WeeklyQuest FindWeeklyById(int id)
    {
        foreach (var quest in Resources.FindObjectsOfTypeAll<WeeklyQuest>())
            if (quest != null && quest.GetInstanceID() == id) return quest;
        return null;
    }

    private void CountFailure(Quest quest)
    {
        var id = quest.GetInstanceID();
        _rerollFailures.TryGetValue(id, out var failures);
        _rerollFailures[id] = failures + 1;
        _nextMutation = Time.unscaledTime + Math.Min(30f, 5f * (failures + 1));
    }

    private static bool IsActionable(QuestEntry entry)
    {
        var quest = entry.Quest;
        if (quest == null || quest.isClaimed) return false;
        try
        {
            if (quest.IsCompleted()) return false;
            return RequirementsAvailable(entry);
        }
        catch { return false; }
    }

    private static bool RequirementsAvailable(QuestEntry entry)
    {
        var quest = entry?.Quest;
        if (quest == null) return false;
        try
        {
            if (!QuestApi.IsSupported(quest) || !QuestApi.EventAvailable(quest)) return false;
            // The native quest remains CanBeCompleted() even when the current Ascension has not
            // purchased Essence of Zynium.  In that state no Zynium can spawn, so treating the
            // quest as actionable only wastes Necklace of Collectables materials forever.
            if (!CanProgressCollectableQuest(quest.GetKind())) return false;
            if (entry.Source == QuestSource.Daily && quest is DailyQuest daily && !daily.CheckIfIsValid())
                return false;
            return quest.CanBeCompleted();
        }
        catch { return false; }
    }

    internal static bool CanProgressCollectableQuest(QuestKind type)
    {
        if (type != QuestKind.PickUpZyniums) return true;
        try
        {
            var inventory = PlayerInventory.instance;
            return inventory != null && double.IsFinite(inventory.zyniumChance) && inventory.zyniumChance > 0;
        }
        catch { return false; }
    }

    private void LogUnavailableZyniumQuest()
    {
        if (Time.unscaledTime < _nextZyniumUnavailableLogAt || CanProgressCollectableQuest(QuestKind.PickUpZyniums))
            return;
        foreach (var entry in _snapshot)
        {
            var quest = entry?.Quest;
            if (quest == null || quest.isClaimed || quest.GetKind() != QuestKind.PickUpZyniums ||
                quest.questCurrentGoal >= quest.questGoal) continue;
            _nextZyniumUnavailableLogAt = Time.unscaledTime + 60f;
            SmartLog.Info(SmartLogCategory.Quest,
                $"Quest deferred: name={quest.name ?? "Zynium collection"}, progress={quest.questCurrentGoal}/{quest.questGoal}; " +
                "current Zynium spawn chance is zero. Automation resumes after Zynium Essence is enabled.");
            return;
        }
    }

    private QuestPlan MakePlan(QuestEntry entry, PortfolioMapChoice portfolio)
    {
        var quest = entry.Quest;
        // Wind Dash kill quests progress only passively with the player's selected ability. They never lock, travel,
        // craft boosters, or join map planning; Daily and Weekly versions reroll once native eligibility is available.
        if (quest.GetKind() == QuestKind.WindDashKills) return null;
        // The first phase of a new set waits only for Complete Daily Quests. A randomly present Portal Weekly cannot run
        // until the Daily-type Weekly is claimed and Portal becomes the finishing phase.
        if (entry.Source == QuestSource.Weekly && quest.GetKind() == QuestKind.GoThroughPortals &&
            Plugin.Config.WeeklyPreferredPairStrategy.Value &&
            _weeklyPreferredTarget != QuestKind.GoThroughPortals) return null;
        var target = ResolveTargetMap(quest, out var density);
        var strategicUnlockBonus = StrategicUnlockBonus(quest);
        var jointQuestCount = 0;
        var requiredMap = QuestApi.RequiredMap(quest);
        if (portfolio?.Map != null && strategicUnlockBonus <= 0 &&
            (requiredMap == null || MapEfficiencyEstimator.SameMap(requiredMap, portfolio.Map)))
        {
            if (portfolio.TryGetEstimate(quest, out var jointEstimate) &&
                MapEfficiencyEstimator.EffectiveDensity(jointEstimate) > 0)
            {
                target = portfolio.Map;
                density = MapEfficiencyEstimator.EffectiveDensity(jointEstimate);
                jointQuestCount = portfolio.ProgressingQuestCount;
            }
            else if (IsMapAnchoringQuest(quest))
            {
        // The composite route already selected the best map across all active targets. An ordinary anchor that cannot
        // progress there cannot retake map control; strategic unlocks are the explicit exception above.
                return null;
            }
        }
        // Bonus-Stage-only enemies do not belong to a standard map and cannot hold the primary lock or block reachable travel.
        if (NeedsTargetMap(quest.GetKind()) && (target == null || density <= 0)) return null;
        var expectedSeconds = EstimateSeconds(quest, entry.Source, target, density);
        var goal = Math.Max(1, quest.questGoal);
        var ratio = Math.Clamp(quest.questCurrentGoal / (double)goal, 0, 1);
        var score = entry.Source switch
        {
            QuestSource.Daily => 125,
            QuestSource.Normal => quest.unlocksUpgrade != null ? 160 : 145,
            QuestSource.Event => 145,
            QuestSource.Weekly => 80,
            _ => 70
        };
        score += (int)(ratio * 45);
        if (target == null || MapEfficiencyEstimator.SameMap(target, MapController.instance?.selectedMap)) score += 25;
        else score -= 8;
        if (jointQuestCount > 1) score += Math.Min(48, (jointQuestCount - 1) * 12);
        score += strategicUnlockBonus;
        score -= Math.Min(70, (int)(expectedSeconds / (entry.Source == QuestSource.Weekly ? 45 : 12)));
        var difficulty = Difficulty(expectedSeconds, entry.Source);
        return new QuestPlan(quest, entry.Source, target, score, difficulty, expectedSeconds, density,
            jointQuestCount, strategicUnlockBonus, jointQuestCount > 0 ? portfolio?.Utility ?? 0 : 0);
    }

    private BaseMap ResolveTargetMap(Quest quest, out double targetEnemiesPerPattern)
    {
        targetEnemiesPerPattern = 0;
        if (quest == null || !NeedsTargetMap(quest.GetKind())) return null;
        var requiredMap = QuestApi.RequiredMap(quest);
        if (requiredMap != null)
        {
            if (!IsStandardAvailableMap(requiredMap)) return null;
            if (quest.GetKind() == QuestKind.VisitLocation)
            {
                var controller = MapController.instance;
                if (MapEfficiencyEstimator.SameMap(requiredMap, controller?.selectedMap) &&
                    (controller?.maps == null || !controller.maps.Any(map =>
                        IsStandardAvailableMap(map) && !MapEfficiencyEstimator.SameMap(map, requiredMap))))
                    return null;
                targetEnemiesPerPattern = 1; // A visit is an arrival action, not a monster-density estimate.
                return requiredMap;
            }
            var requiredEstimate = MapEfficiencyEstimator.EvaluateForQuest(requiredMap, quest);
            targetEnemiesPerPattern = MapEfficiencyEstimator.EffectiveDensity(requiredEstimate);
            return targetEnemiesPerPattern > 0 ? requiredMap : null;
        }
        var remaining = Math.Max(1, quest.questGoal - quest.questCurrentGoal);
        var efficiency = quest.GetKind() == QuestKind.GetMaterials
            ? MapEfficiencyEstimator.BestForAnyMaterial(remaining)
            : IsUnrestrictedKillObjective(quest)
                ? MapEfficiencyEstimator.BestForPassiveKillAndMaterials(quest)
                : MapEfficiencyEstimator.BestForQuest(quest, AvailableKillCreditPerEnemy(quest));
        if (efficiency == null) return null;
        targetEnemiesPerPattern = MapEfficiencyEstimator.EffectiveDensity(efficiency);
        return efficiency.Map;
    }

    private static bool NeedsTargetMap(QuestKind type) =>
        NeedsEnemyMap(type) || type is QuestKind.GetMaterials or QuestKind.VisitLocation;

    private PortfolioMapChoice ResolvePortfolioMap()
    {
        if (!Plugin.Config.JointMapPlanningEnabled.Value) return null;
        // Joint map choice requires at least one concrete kill target or material quest anchor. With only generic kills,
        // choose from missing-material value plus farming efficiency instead of locking to raw enemy volume.
        var now = Time.unscaledTime;
        if (!_snapshot.Any(entry => IsActionable(entry) && !IsDeferredFromPrimary(entry.Quest, now) &&
                                   IsMapAnchoringQuest(entry.Quest))) return null;
        var controller = MapController.instance;
        if (controller?.maps == null) return null;
        var horizon = Math.Max(60, Plugin.Config.JointMapPlanningHorizonSeconds.Value);
        var patternsPerMinute = Math.Max(1, Plugin.Config.ExpectedEnemyPatternsPerMinute.Value);
        PortfolioMapChoice best = null;
        PortfolioMapChoice sticky = null;
        var stickyMap = _portfolio?.Map ?? controller.selectedMap;

        for (var i = 0; i < controller.maps.Length; i++)
        {
            var map = controller.maps[i];
            if (!IsStandardAvailableMap(map)) continue;
            var contributions = new List<PortfolioContribution>();
            var estimates = new Dictionary<int, MapEfficiency>();
            foreach (var entry in _snapshot)
            {
                if (!IsActionable(entry) || IsDeferredFromPrimary(entry.Quest, now)) continue;
                var quest = entry.Quest;
                var estimate = PortfolioEstimate(map, quest);
                var density = MapEfficiencyEstimator.EffectiveDensity(estimate);
                if (density <= 0) continue;
                var remaining = Math.Max(1, quest.questGoal - quest.questCurrentGoal);
                var credit = quest.GetKind() == QuestKind.GetMaterials
                    ? MapEfficiencyEstimator.MaterialProgressPerEligibleKill(estimate.TargetsGiant)
                    : AvailableKillCreditPerEnemy(quest);
                if (credit <= 0) continue;
                var contributionDensity = estimate.TargetsGiant
                    ? MapEfficiencyEstimator.EstimatedTargetKillsPerMinute(density, true,
                        includeTemporaryShards: false) / patternsPerMinute
                    : density;
                contributions.Add(new PortfolioContribution(QuestPriority(entry), remaining,
                    contributionDensity, credit));
                estimates[quest.GetInstanceID()] = estimate;
            }
            if (contributions.Count == 0) continue;
            var utility = StrategyMath.PortfolioUtility(contributions, patternsPerMinute, horizon,
                MapEfficiencyEstimator.EstimatedTravelSeconds(map));
            var choice = new PortfolioMapChoice(map, utility, contributions.Count, estimates);
            if (MapEfficiencyEstimator.SameMap(map, stickyMap)) sticky = choice;
            if (best == null || utility > best.Utility + 0.0001 ||
                Math.Abs(utility - best.Utility) <= 0.0001 && MapEfficiencyEstimator.SameMap(map, controller.selectedMap))
                best = choice;
        }
        if (best == null)
        {
            _lastPortfolioRouteId = null;
            return null;
        }

        var chosen = best;
        var minimumGain = Math.Clamp(Plugin.Config.JointMapSwitchMinimumGainPercent.Value, 0, 500) / 100.0;
        if (sticky?.Map != null && !MapEfficiencyEstimator.SameMap(sticky.Map, best.Map) && sticky.Utility > 0 &&
            best.Utility < sticky.Utility * (1 + minimumGain))
            chosen = sticky;

        LogPortfolioRoute(chosen, best, sticky, minimumGain);
        return chosen;
    }

    private void LogPortfolioRoute(PortfolioMapChoice chosen, PortfolioMapChoice best,
        PortfolioMapChoice sticky, double minimumGain)
    {
        var id = chosen?.Map?.id ?? chosen?.Map?.name;
        if (string.IsNullOrEmpty(id) || string.Equals(id, _lastPortfolioRouteId, StringComparison.Ordinal)) return;
        var previous = _lastPortfolioRouteId;
        _lastPortfolioRouteId = id;
        var keptForStickiness = sticky != null && best != null &&
                                 MapEfficiencyEstimator.SameMap(chosen.Map, sticky.Map) &&
                                 !MapEfficiencyEstimator.SameMap(chosen.Map, best.Map);
        var detail = keptForStickiness
            ? $"; candidate={best.Map?.name}, utility={best.Utility:0.###} does not exceed current by {minimumGain * 100:0.#}%"
            : previous == null ? string.Empty : "; switched after active quests or value structure changed";
        SmartLog.Info(SmartLogCategory.Quest,
            $"Joint quest route: map={chosen.Map?.name ?? "unknown"}, horizon utility={chosen.Utility:0.###}, " +
            $"progressing quests={chosen.ProgressingQuestCount}.");
    }

    private static MapEfficiency PortfolioEstimate(BaseMap map, Quest quest)
    {
        if (quest == null || !QuestApi.CanProgressOnMap(quest, map)) return null;
        // Critical hits progress against every enemy. They stay in the parallel execution group for Critical Sword
        // coverage and progress tracking, but must not influence the chosen route or suppress a concrete enemy target.
        if (quest.GetKind() is QuestKind.WindDashKills or QuestKind.DoCriticals) return null;
        if (quest.GetKind() == QuestKind.GetMaterials) return MapEfficiencyEstimator.EvaluateForMaterialQuest(map);
        if (!NeedsEnemyMap(quest.GetKind())) return null;
        return MapEfficiencyEstimator.EvaluateForQuest(map, quest);
    }

    private double QuestPriority(QuestEntry entry)
    {
        var basePriority = entry.Source switch
        {
            QuestSource.Daily => 1.25,
            QuestSource.Normal => entry.Quest?.unlocksUpgrade != null ? 1.60 : 1.45,
            QuestSource.Event => 1.45,
            QuestSource.Weekly => 0.80,
            _ => 0.70
        };
        var mapWeight = entry.Quest?.GetKind() switch
        {
            QuestKind.KillShinyEnemies => 0.10,
            QuestKind.KillEnemiesWithRageMode when IsUnrestrictedKillObjective(entry.Quest) => 0.10,
            QuestKind.KillEnemiesWithArrows when entry.Quest.enemyToKill == null && entry.Quest.enemyType == null => 0.25,
            QuestKind.KillEnemies when entry.Quest.enemyToKill == null && entry.Quest.enemyType == null => 0.50,
            _ => 1.0
        };
        return basePriority * mapWeight + StrategicUnlockBonus(entry.Quest) / 100.0;
    }

    private static int SourcePriorityRank(QuestSource source) => source switch
    {
        QuestSource.Normal => 3,
        QuestSource.Event => 3,
        QuestSource.Daily => 2,
        QuestSource.Weekly => 1,
        _ => 0
    };

    private static bool IsMapAnchoringQuest(Quest quest) => quest != null && quest.GetKind() != QuestKind.WindDashKills &&
        (quest.GetKind() == QuestKind.GetMaterials || NeedsEnemyMap(quest.GetKind()) && !IsUnrestrictedKillObjective(quest));

    private static bool IsUnrestrictedKillObjective(Quest quest)
    {
        if (quest == null || !NeedsEnemyMap(quest.GetKind())) return false;
        if (QuestApi.RequiredMap(quest) != null) return false;
        if (quest.GetKind() is QuestKind.KillFlyers or QuestKind.KillGiants or QuestKind.KillEnemiesOfType)
            return false;
        try { return quest.enemyToKill == null && quest.enemyType == null; }
        catch { return false; }
    }

    private int StrategicUnlockBonus(Quest quest)
    {
        if (!Plugin.Config.StrategicUnlockPlanningEnabled.Value || quest == null) return 0;
        if (Time.unscaledTime >= _nextStrategicUnlockScan) RebuildStrategicUnlockBonuses();
        var bonus = 0;
        try { _questChainUnlockBonuses.TryGetValue(quest.GetInstanceID(), out bonus); }
        catch { /* Keep upgrade dependency bonus below. */ }
        var upgrade = quest.unlocksUpgrade;
        if (upgrade == null) return bonus;
        try { if (upgrade.bought) return bonus; }
        catch { return bonus; }
        return _strategicUnlockBonuses.TryGetValue(StrategicKey(upgrade), out var materialBonus)
            ? Math.Max(bonus, materialBonus)
            : bonus;
    }

    private void RebuildStrategicUnlockBonuses()
    {
        _nextStrategicUnlockScan = Time.unscaledTime + 30f;
        _strategicUnlockBonuses.Clear();
        _questChainUnlockBonuses.Clear();
        var materialWeights = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var entry in StrategicItems())
        {
            var item = _catalog.Get(entry.Kind);
            if (item == null) continue;
            try
            {
                var requirements = item.GetRequirements();
                if (requirements == null) continue;
                for (var i = 0; i < requirements.Length; i++)
                {
                    var material = requirements[i]?.material;
                    if (material == null || requirements[i].divinityPoints) continue;
                    var key = MaterialKey(material);
                    if (string.IsNullOrEmpty(key)) continue;
                    if (!materialWeights.TryGetValue(key, out var weights))
                    {
                        weights = new List<int>();
                        materialWeights[key] = weights;
                    }
                    weights.Add(entry.Bonus);
                }
            }
            catch { /* Missing late-game items simply do not create a dependency yet. */ }
        }

        try
        {
            foreach (var enemy in Resources.FindObjectsOfTypeAll<Enemy>())
            {
                if (enemy?.upgradeToUnlockIt == null || enemy.drop == null) continue;
                if (!materialWeights.TryGetValue(MaterialKey(enemy.drop), out var weights)) continue;
                var key = StrategicKey(enemy.upgradeToUnlockIt);
                var bonus = StrategyMath.StrategicUnlockBonus(weights);
                if (!_strategicUnlockBonuses.TryGetValue(key, out var current) || bonus > current)
                    _strategicUnlockBonuses[key] = bonus;
            }
        }
        catch { /* Rebuilt on the next interval after Unity finishes loading resources. */ }

        RebuildQuestChainUnlockBonuses();
    }

    private void RebuildQuestChainUnlockBonuses()
    {
        try
        {
            var quests = Resources.FindObjectsOfTypeAll<Quest>();
            var downstreamByBundle = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var downstream in quests)
            {
                if (downstream?.bundleRequired == null) continue;
                try { if (downstream.isClaimed) continue; }
                catch { continue; }
                var key = StrategicKey(downstream.bundleRequired);
                if (string.IsNullOrEmpty(key)) continue;
                downstreamByBundle.TryGetValue(key, out var count);
                downstreamByBundle[key] = count + 1;
            }

            foreach (var unlocker in quests)
            {
                if (unlocker?.unlocksUpgrade == null) continue;
                AddQuestChainUnlockBonus(unlocker, unlocker.unlocksUpgrade, downstreamByBundle);
            }

                // Some versions store only the reverse relation in Upgrade.unlockedWithQuest; recognize both forms.
            foreach (var upgrade in Resources.FindObjectsOfTypeAll<Upgrade>())
            {
                if (upgrade?.unlockedWithQuest == null) continue;
                AddQuestChainUnlockBonus(upgrade.unlockedWithQuest, upgrade, downstreamByBundle);
            }
        }
        catch { /* Preserve material-unlock priorities already built while Unity resources change scenes. */ }
    }

    private void AddQuestChainUnlockBonus(Quest unlocker, Upgrade bundle,
        Dictionary<string, int> downstreamByBundle)
    {
        if (unlocker == null || bundle == null) return;
        try
        {
            if (unlocker.isClaimed || bundle.bought) return;
            if (!downstreamByBundle.TryGetValue(StrategicKey(bundle), out var downstreamCount) || downstreamCount <= 0)
                return;
            var bonus = 1000 + Math.Min(500, downstreamCount * 50);
            var questId = unlocker.GetInstanceID();
            if (!_questChainUnlockBonuses.TryGetValue(questId, out var current) || bonus > current)
                _questChainUnlockBonuses[questId] = bonus;
        }
            catch { /* One stale resource does not affect other quest chains. */ }
    }

    private static IEnumerable<(SmartItem Kind, int Bonus)> StrategicItems()
    {
        yield return (SmartItem.Whetstone, 300);
        yield return (SmartItem.DeathwaveScepter, 260);
        yield return (SmartItem.BidimensionalStaff, 240);
        yield return (SmartItem.RagePill, 210);
        yield return (SmartItem.ShardsNecklace, 200);
        yield return (SmartItem.AlternateDimensionStaff, 180);
        yield return (SmartItem.RandomBoxStaff, 170);
        yield return (SmartItem.NecklaceOfCollectables, 170);
        yield return (SmartItem.RandomStone, 150);
        yield return (SmartItem.KeyManifest, 150);
        yield return (SmartItem.CriticalSword, 140);
        yield return (SmartItem.SoulsCompass, 120);
        yield return (SmartItem.SoulsSpyglass, 120);
    }

    private static string StrategicKey(Upgrade upgrade)
    {
        if (upgrade == null) return string.Empty;
        var name = CraftableCatalog.Normalize(upgrade.name);
        return string.IsNullOrEmpty(name) ? $"id:{upgrade.GetInstanceID()}" : name;
    }

    private static string MaterialKey(Drop material)
    {
        if (material == null) return string.Empty;
        var name = CraftableCatalog.Normalize(material.name);
        return string.IsNullOrEmpty(name) ? CraftableCatalog.Normalize(material.localizedName) : name;
    }

    private static bool NeedsEnemyMap(QuestKind type) => type is
        QuestKind.KillEnemies or QuestKind.KillEnemiesWithArrows or QuestKind.KillFlyers or QuestKind.KillGiants or
        QuestKind.KillEnemiesWithRageMode or QuestKind.KillEnemiesWithCharacter or QuestKind.KillEnemiesOfType or
        QuestKind.KillShinyEnemies or QuestKind.WindDashKills or QuestKind.KillEnemiesInLocation;

    internal static bool IsStandardAvailableMap(BaseMap map)
    {
        if (map == null) return false;
        try
        {
            if (!map.IsAvailable() || map.enemies == null || map.enemies.Length == 0) return false;
            var id = CraftableCatalog.Normalize((map.id ?? string.Empty) + (map.name ?? string.Empty));
            var blocked = new[] { "village", "house", "temple", "tavern", "casino", "tower", "mill", "bonus", "boss", "ascending", "grapple", "cutscene", "passedout" };
            return !blocked.Any(id.Contains);
        }
        catch { return false; }
    }

    internal static bool TargetsGiant(Quest quest)
    {
        if (quest == null) return false;
        if (quest.GetKind() == QuestKind.KillGiants) return true;
        if (MapEfficiencyEstimator.CurrentStage(quest.enemyToKill)?.isGiant == true) return true;
        try
        {
            var typeName = CraftableCatalog.Normalize((quest.enemyType?.name ?? string.Empty) +
                (quest.enemyType?.localizedName ?? string.Empty));
            return typeName.Contains("giant", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    internal static bool TargetCannotBeKilledWithArrows(Quest quest)
    {
        if (quest == null || !Plugin.Config.AssumeBowAutomation.Value) return false;
        if (quest.GetKind() is QuestKind.KillEnemiesWithRageMode or QuestKind.WindDashKills) return false;
        var target = MapEfficiencyEstimator.CurrentStage(quest.enemyToKill);
        if (target?.cantBeKilledWithArrows != true) return false;
        if (target.isGiant && Plugin.Config.GiantRapidAttackEnabled.Value) return false;
        return !StrategyBehaviour.CanAutomateWithWindDash(target);
    }

    private double EstimateSeconds(Quest quest, QuestSource source, BaseMap target, double density)
    {
        if (quest == null) return double.MaxValue;
        var remaining = Math.Max(0d, quest.questGoal - quest.questCurrentGoal);
        if (remaining == 0) return 0;
        var travel = MapEfficiencyEstimator.EstimatedTravelSeconds(target);
        var type = quest.GetKind();

        if (type == QuestKind.KillEnemiesWithRageMode)
        {
            var fallbackRage = EstimateRageKillQuestSeconds(quest, density, travel);
            return QuestApi.UsesUnboostedProgress(quest) ? fallbackRage :
                CalibratedExpectedSeconds(type, remaining, fallbackRage);
        }

        if (NeedsEnemyMap(type))
        {
            var fallbackKill = EstimateTimedKillQuestSeconds(quest, density, travel);
            return !QuestApi.UsesUnboostedProgress(quest) && SupportsRuntimeCalibration(type)
                ? CalibratedExpectedSeconds(type, remaining, fallbackKill)
                : fallbackKill;
        }
        var fallback = type switch
        {
            QuestKind.GetMaterials => EstimateMaterialQuestSeconds(remaining, target, density, travel),
            QuestKind.UseRageMode => remaining * 10.0,
            QuestKind.PickUpCoins or QuestKind.PickUpRubies or QuestKind.PickUpSapphires or QuestKind.PickUpEmeralds or
                QuestKind.PickUpGemstones or QuestKind.PickUpDiamonds or QuestKind.PickUpZyniums => Math.Max(30, Math.Ceiling(remaining / AvailableCollectableCredit(quest)) / 20.0),
            QuestKind.HitRandomBoxes or QuestKind.GetCoinsFromRandomBox =>
                Math.Ceiling(remaining / AvailableRandomBoxCredit(quest)) * (ItemUsable(SmartItem.RandomStone) ? 79.0 / 1.4 : 79.0),
            QuestKind.HitRandomSilverBoxes or QuestKind.HitRandomGoldenBoxes => 1200 + remaining * 30.0,
            QuestKind.BonusStage or QuestKind.BonusStageSections => 900 + remaining * 30.0,
            QuestKind.ChestHuntChests => source == QuestSource.Weekly ? 1800 + remaining * 5.0 : 600 + remaining * 5.0,
            QuestKind.Boost => EstimateBoostQuestSeconds(remaining),
            QuestKind.CraftTemporaryItems => StrategyBehaviour.CanCompleteCraftQuestWithDragonEggs(remaining)
                ? Math.Max(1, remaining * 0.5)
                : 600 + remaining * 15.0,
            QuestKind.DoCriticals => Math.Max(60, remaining / 10.0),
            QuestKind.GoThroughPortals => EstimatePortalQuestSeconds(remaining),
            QuestKind.VisitLocation => travel + EstimatePortalQuestSeconds(
                Math.Max(1, remaining * 2 - (MapEfficiencyEstimator.SameMap(target,
                    MapController.instance?.selectedMap) ? 0 : 1))),
            QuestKind.CompleteDailyQuests => 60,
            QuestKind.FindArmoryItems or QuestKind.DigDigPiles or QuestKind.CompleteAscendingHeights or
                QuestKind.CompleteGrappleRuns => 1800 + remaining * 300.0,
            _ => source == QuestSource.Weekly ? 1200 : 300
        };
        return !QuestApi.UsesUnboostedProgress(quest) && SupportsRuntimeCalibration(type)
            ? CalibratedExpectedSeconds(type, remaining, fallback)
            : fallback;
    }

    private double CalibratedExpectedSeconds(QuestKind type, double remaining, double fallback)
    {
        if (!Plugin.Config.QuestRuntimeCalibrationEnabled.Value ||
            !_runtimeCalibrations.TryGetValue(type, out var calibration) ||
            calibration.Samples < Math.Max(1, Plugin.Config.QuestRuntimeCalibrationMinimumSamples.Value))
            return fallback;
        var empirical = remaining * calibration.SecondsPerProgress;
        if (!double.IsFinite(empirical) || empirical <= 0) return fallback;
        // Live session measurements may correct the fixed model, but one paused minigame or burst cannot drive it to extremes.
        return Math.Clamp(empirical, Math.Max(1, fallback * 0.25), Math.Max(1, fallback * 4));
    }

    private static bool SupportsRuntimeCalibration(QuestKind type) => type is
        QuestKind.KillEnemies or QuestKind.KillEnemiesWithArrows or QuestKind.KillFlyers or QuestKind.KillGiants or
        QuestKind.KillEnemiesWithRageMode or QuestKind.KillEnemiesWithCharacter or QuestKind.KillEnemiesOfType or
        QuestKind.KillShinyEnemies or QuestKind.KillEnemiesInLocation or QuestKind.GetMaterials or QuestKind.UseRageMode or
        QuestKind.PickUpCoins or QuestKind.PickUpRubies or QuestKind.PickUpSapphires or
        QuestKind.PickUpEmeralds or QuestKind.PickUpGemstones or QuestKind.PickUpDiamonds or QuestKind.PickUpZyniums or
        QuestKind.HitRandomBoxes or QuestKind.GetCoinsFromRandomBox or QuestKind.HitRandomSilverBoxes or
        QuestKind.HitRandomGoldenBoxes or QuestKind.BonusStage or QuestKind.BonusStageSections or
        QuestKind.ChestHuntChests or QuestKind.Boost or QuestKind.CraftTemporaryItems or QuestKind.DoCriticals or
        QuestKind.GoThroughPortals;

    private double EstimateRageKillQuestSeconds(Quest quest, double density, double travel)
    {
        var remaining = Math.Max(0, quest.questGoal - quest.questCurrentGoal);
        if (remaining <= 0) return 0;
        var killsPerMinute = MapEfficiencyEstimator.EstimatedTargetKillsPerMinute(density,
            TargetsGiant(quest));
        var baseCredit = 1.0;
        try { baseCredit = Math.Max(1, PlayerInventory.instance?.questProgressPerKill ?? 1); }
        catch { /* Keep base quest credit during scene races. */ }

        var bonuses = new List<RageQuestTimedBonus>();
        if (QuestApi.UsesUnboostedProgress(quest)) baseCredit = 1;
        else
        {
            foreach (var (kind, bonus) in CraftableEffectModel.KillStaffsDescending())
                AddRageStaffForecast(kind, bonus, travel, ref baseCredit, bonuses);
        }

        var runtime = RageQuestEstimator.Capture(_catalog, quest.GetInstanceID(), travel);
        var estimate = RageQuestEstimator.EstimateKill(remaining, killsPerMinute, baseCredit, runtime, bonuses);
        return travel + estimate.Seconds;
    }

    private void AddRageStaffForecast(SmartItem kind, int bonus, double travel, ref double baseCredit,
        List<RageQuestTimedBonus> bonuses)
    {
        var item = _catalog.Get(kind);
        if (item == null) return;
        try
        {
            var coverage = PermanentClientBonuses.CoverageSeconds(item);
            var active = item.IsActive() || coverage > 0.05;
            if (active)
            {
                // PlayerInventory already contains active staff credit; move it into a timed effect so
                // expiration during later rage/cooldown windows is represented correctly.
                baseCredit = Math.Max(1, baseCredit - bonus);
                coverage = Math.Max(0, coverage - travel);
                if (coverage > 0.05) bonuses.Add(new RageQuestTimedBonus(bonus, coverage));
                return;
            }
            if (!item.TabVisible() || !item.ExtraCondition() || item.HowManyCanCraft() <= 0) return;
            double duration;
            try { duration = item.GetDuration(); }
            catch { duration = item.duration; }
            if (duration > 0.05) bonuses.Add(new RageQuestTimedBonus(bonus, duration));
        }
        catch { /* A single unavailable staff must not invalidate the quest estimate. */ }
    }

    private static double EstimateBoostQuestSeconds(double remaining)
    {
        try
        {
            var cooldown = AbilitiesManager.instance?.boost?.GetCooldown() ?? 3.75f;
            return remaining * Math.Clamp(cooldown, 0.25f, 30f);
        }
        catch { return remaining * 3.75; }
    }

    private static double EstimatePortalQuestSeconds(double remaining)
        => PortalTimingEstimator.PortalQuestSeconds(remaining);

    private double AvailableKillCreditPerEnemy(Quest quest = null)
    {
        var live = 1;
        try { live = Math.Max(1, PlayerInventory.instance?.questProgressPerKill ?? 1); }
        catch { /* Game initialization races use the base value. */ }
        if (QuestApi.UsesUnboostedProgress(quest)) return 1;

        var remaining = quest == null ? double.PositiveInfinity :
            Math.Max(0, quest.questGoal - quest.questCurrentGoal);
        var potential = 0;
        var forecastCredit = (double)live;
        foreach (var definition in KillStaffDefinitions())
        {
            if (ItemActive(definition.Kind) || KillStaffCraftableUses(definition.Kind) <= 0) continue;
            if (!StrategyMath.KillStaffSavesEnough(remaining, forecastCredit,
                    definition.Kind, definition.Bonus)) continue;
            potential += definition.Bonus;
            forecastCredit += definition.Bonus;
        }
        return StrategyMath.ForecastQuestCredit(live, potential, false);
    }

    private double EstimateTimedKillQuestSeconds(Quest quest, double density, double travel)
    {
        var remaining = Math.Max(0d, quest.questGoal - quest.questCurrentGoal);
        var killsPerMinute = MapEfficiencyEstimator.EstimatedTargetKillsPerMinute(density,
            TargetsGiant(quest));
        if (QuestApi.UsesUnboostedProgress(quest))
            return travel + Math.Ceiling(remaining) / killsPerMinute * 60;

        var baseCredit = 1.0;
        try { baseCredit = Math.Max(1, PlayerInventory.instance?.questProgressPerKill ?? 1); }
        catch { /* Keep base quest credit during scene races. */ }

        var bonuses = new List<KillQuestTimedBonus>();
        foreach (var definition in KillStaffDefinitions())
        {
            var item = _catalog.Get(definition.Kind);
            if (item == null || !ItemActive(definition.Kind)) continue;
        // Live PlayerInventory already includes active staffs. Remove them to recover base credit, then model expiry from remaining time.
            baseCredit = Math.Max(1, baseCredit - definition.Bonus);
            try
            {
                var coverage = Math.Max(0, PermanentClientBonuses.CoverageSeconds(item) - travel);
                if (coverage > 0.05) bonuses.Add(new KillQuestTimedBonus(definition.Bonus, coverage));
            }
            catch { /* Ignore only the remaining window of one invalid item. */ }
        }

        var decisionCredit = baseCredit + bonuses.Sum(x => x.Bonus);
        foreach (var definition in KillStaffDefinitions())
        {
            if (ItemActive(definition.Kind)) continue;
            var uses = KillStaffCraftableUses(definition.Kind);
            if (uses <= 0 || !StrategyMath.KillStaffSavesEnough(remaining, decisionCredit,
                    definition.Kind, definition.Bonus)) continue;
            var item = _catalog.Get(definition.Kind);
            if (item == null) continue;
            double duration;
            try { duration = Math.Max(0, item.GetDuration()); }
            catch
            {
                try { duration = Math.Max(0, item.duration); }
                catch { duration = 0; }
            }
            if (duration <= 0.05) continue;
            bonuses.Add(new KillQuestTimedBonus(definition.Bonus, duration * uses));
            decisionCredit += definition.Bonus;
        }

        var estimate = KillQuestEstimator.Estimate(remaining, killsPerMinute, baseCredit, bonuses);
        return travel + estimate.Seconds;
    }

    private int KillStaffCraftableUses(SmartItem kind)
    {
        var item = _catalog.Get(kind);
        if (item == null) return 0;
        try
        {
            if (!item.TabVisible() || !item.ExtraCondition()) return 0;
            var craftable = item.HowManyCanCraft();
            if (!double.IsFinite(craftable) || craftable < 1) return 0;
            return craftable >= int.MaxValue ? int.MaxValue : Math.Max(0, (int)Math.Floor(craftable));
        }
        catch { return 0; }
    }

    private static IEnumerable<(SmartItem Kind, int Bonus)> KillStaffDefinitions()
    {
        foreach (var staff in CraftableEffectModel.KillStaffsDescending()) yield return staff;
    }

    private double AvailableCollectableCredit(Quest quest)
    {
        var live = 1;
        try { live = Math.Max(1, PlayerInventory.instance?.collectablesQuestsProgress ?? 1); }
        catch { /* Use base value. */ }
        var potential = !ItemActive(SmartItem.NecklaceOfCollectables) && ItemUsable(SmartItem.NecklaceOfCollectables) ? 8 : 0;
        return StrategyMath.ForecastQuestCredit(live, potential, QuestApi.UsesUnboostedProgress(quest));
    }

    private double AvailableRandomBoxCredit(Quest quest)
    {
        var live = 1;
        try { live = Math.Max(1, PlayerInventory.instance?.randomBoxQuestsProgress ?? 1); }
        catch { /* Use base value. */ }
        var potential = !ItemActive(SmartItem.RandomBoxStaff) && ItemUsable(SmartItem.RandomBoxStaff) ? 5 : 0;
        return StrategyMath.ForecastQuestCredit(live, potential, QuestApi.UsesUnboostedProgress(quest));
    }

    private static double EstimateMaterialQuestSeconds(double remaining, BaseMap target, double density, double travel)
    {
        if (target == null || density <= 0) return Math.Max(90, remaining * 6);
        var estimate = MapEfficiencyEstimator.EvaluateForMaterialQuest(target);
        var yield = MapEfficiencyEstimator.MaterialProgressPerEligibleKill(estimate.TargetsGiant);
        if (yield <= 0) return double.MaxValue;
        var killsPerMinute = MapEfficiencyEstimator.EstimatedTargetKillsPerMinute(density,
            estimate.TargetsGiant, includeTemporaryShards: false);
        return travel + remaining / yield / killsPerMinute * 60;
    }

    private bool ItemUsable(SmartItem kind)
    {
        var item = _catalog.Get(kind);
        if (item == null) return false;
        try
        {
            return item.IsActive() || (item.TabVisible() && item.ExtraCondition() &&
                item.HowManyCanCraft() > Math.Max(0, Plugin.Config.QuestBoosterReserveUses.Value));
        }
        catch { return false; }
    }

    private bool ItemActive(SmartItem kind)
    {
        var item = _catalog.Get(kind);
        if (item == null) return false;
        try { return item.IsActive() || item.currentTime > 0.05; }
        catch { return false; }
    }

    internal static bool IsCollabQuest(Quest quest)
    {
        if (quest == null) return false;
        try { return quest.fromCollab != null; }
        catch { return false; }
    }

    private bool IsCurrentWeeklyTarget(QuestKind type) => type == _weeklyPreferredTarget;

    private void InitializeWeeklyTargetStage()
    {
        if (_weeklyTargetStageInitialized || !Plugin.Config.WeeklyPreferredPairStrategy.Value) return;
        _weeklyTargetStageInitialized = true;
        var active = _snapshot.Where(x => x.Source == QuestSource.Weekly && x.Quest != null &&
                                          !x.Quest.isClaimed).ToArray();
        var daily = active.FirstOrDefault(x => x.Quest.GetKind() == QuestKind.CompleteDailyQuests);
        var portal = active.FirstOrDefault(x => x.Quest.GetKind() == QuestKind.GoThroughPortals);
        // On a mid-set start, finish a Portal quest that already has progress or is the only quest left. Otherwise treat
        // the set as new and seek Complete Daily Quests first.
        var resumePortal = portal?.Quest != null &&
                           (portal.Quest.questCurrentGoal > 0 || active.Length == 1 && daily == null);
        SetWeeklyPreferredTarget(resumePortal ? QuestKind.GoThroughPortals : QuestKind.CompleteDailyQuests,
            resumePortal
                ? "Detected an in-progress Portal Weekly; continue finishing this set"
                : "Start the current Weekly set from Complete Daily Quests");
    }

    private void AdvanceWeeklyTargetAfterClaim(QuestKind claimedType)
    {
        if (!Plugin.Config.WeeklyPreferredPairStrategy.Value) return;
        if (claimedType == QuestKind.CompleteDailyQuests)
        {
            SetWeeklyPreferredTarget(QuestKind.GoThroughPortals,
                "Complete Daily Quests was claimed; same-set types cannot repeat, so switch to Go Through Portals");
        }
        else if (claimedType == QuestKind.GoThroughPortals)
        {
            SetWeeklyPreferredTarget(QuestKind.CompleteDailyQuests,
                "Go Through Portals was claimed; this set is complete and the next starts from Complete Daily Quests");
        }
    }

    private void SetWeeklyPreferredTarget(QuestKind target, string reason)
    {
        _weeklyTargetStageInitialized = true;
        _weeklyPreferredTarget = target;
        _weeklyPreferredTargetLogged = false;
        SmartLog.Info(SmartLogCategory.Quest, $"Weekly phase target changed to {target}.");
    }

    private bool ShouldForceReroll(QuestKind type, QuestSource source)
    {
        if (source == QuestSource.Weekly)
            return type is QuestKind.GoThroughPortals or QuestKind.ChestHuntChests or QuestKind.BonusStage or
                QuestKind.BonusStageSections or QuestKind.CompleteAscendingHeights or QuestKind.CompleteGrappleRuns or
                QuestKind.UseRageMode;
        var cannotActivelyAutomate = type is QuestKind.WindDashKills or QuestKind.FindArmoryItems or
            QuestKind.DigDigPiles or QuestKind.CompleteAscendingHeights or QuestKind.CompleteGrappleRuns;
        // Use-Rage quests mostly wait for a long cooldown and compete with Rage kills and Souls events for pill materials.
        // Strategy always prefers quests that parallel specified kills, so global slow history cannot protect this type.
        if (type == QuestKind.UseRageMode) return true;
        if (HasAdaptiveDailyRerollThreshold) return cannotActivelyAutomate;
        var coldStartHighOpportunityCost = type is
            QuestKind.Boost or QuestKind.HitRandomBoxes or QuestKind.GetCoinsFromRandomBox or
            QuestKind.HitRandomSilverBoxes or QuestKind.HitRandomGoldenBoxes or QuestKind.ChestHuntChests or
            QuestKind.BonusStage or QuestKind.BonusStageSections or QuestKind.CompleteAscendingHeights or
            QuestKind.CompleteGrappleRuns or QuestKind.FindArmoryItems or QuestKind.DigDigPiles or
            QuestKind.UseRageMode or QuestKind.WindDashKills;
        return cannotActivelyAutomate || coldStartHighOpportunityCost;
    }

    private bool ShouldRerollDailyRageQuest(Quest quest, double density, out string keepReason)
    {
        keepReason = string.Empty;
        if (quest == null || density <= 0) return true;
        try
        {
            var rage = RageModeManager.instance;
            if (rage == null) return true;
            var active = rage.currentState != RageModeManager.RageModeStates.NotActive;
            var activeRemaining = active
                ? Math.Max(0, StrategyBehaviour.Current?.EstimatedRageSecondsRemaining ?? rage.GetDuration())
                : 0;
            var currentCd = active ? Math.Max(0, rage.currentCd) : Math.Max(0, rage.currentCd);
            var duration = Math.Max(1, rage.GetDuration());
            var fullCd = Math.Max(duration, rage.GetCD());
            var runtime = new RageQuestRuntime(activeRemaining, currentCd, fullCd, duration, 0);
            var remaining = Math.Max(0, quest.questGoal - quest.questCurrentGoal);
            var killsPerMinute = MapEfficiencyEstimator.EstimatedTargetKillsPerMinute(density,
                TargetsGiant(quest), includeTemporaryShards: false);
            var liveCredit = Math.Max(1, PlayerInventory.instance?.questProgressPerKill ?? 1);
            var estimate = RageQuestEstimator.EstimateKill(remaining, killsPerMinute, liveCredit, runtime);
            var opportunityBudget = Math.Clamp(DailyRerollThresholdSeconds(), 30, 180);
            var fitsOneNaturalWindow = estimate.RageWindows <= 1 &&
                                       double.IsFinite(estimate.Seconds) &&
                                       estimate.Seconds <= opportunityBudget;
            if (!fitsOneNaturalWindow) return true;

            keepReason = active
                ? $"Rage is active and completion is expected within this {estimate.Seconds:0}s window"
                : $"Natural cooldown={currentCd:0}s and total completion={estimate.Seconds:0}s, below reroll opportunity cost={opportunityBudget:0}s";
            return false;
        }
        catch
        {
            return true;
        }
    }

    private bool IsStalledDailyEventQuest(Quest quest)
    {
        if (quest == null || !IsStallSensitiveDailyType(quest.GetKind())) return false;
        try
        {
            if (!_progressObservations.TryGetValue(quest.GetInstanceID(), out var observation)) return false;
            return Time.unscaledTime - observation.ProgressAt >=
                   Math.Max(20f, Plugin.Config.QuestStallSeconds.Value);
        }
        catch { return false; }
    }

    private static bool IsStallSensitiveDailyType(QuestKind type) => type is
        QuestKind.HitRandomBoxes or QuestKind.HitRandomSilverBoxes or QuestKind.HitRandomGoldenBoxes or
        QuestKind.GetCoinsFromRandomBox or QuestKind.BonusStage or QuestKind.BonusStageSections or
        QuestKind.ChestHuntChests or QuestKind.CompleteAscendingHeights or QuestKind.CompleteGrappleRuns or
        QuestKind.FindArmoryItems or QuestKind.DigDigPiles or QuestKind.Boost or QuestKind.GetMaterials;

    private static bool IsPassiveCollectableDailyType(QuestKind type) => type is
        QuestKind.PickUpCoins or QuestKind.PickUpGemstones or QuestKind.PickUpDiamonds or
        QuestKind.PickUpEmeralds or QuestKind.PickUpRubies or QuestKind.PickUpSapphires or
        QuestKind.PickUpZyniums;

    private static bool CurrentNonStandardMapContainsTarget(Quest quest)
    {
        if (quest == null || !NeedsEnemyMap(quest.GetKind())) return false;
        try
        {
            var current = MapController.instance?.selectedMap;
            if (current == null || IsStandardAvailableMap(current)) return false;
            var estimate = MapEfficiencyEstimator.EvaluateForQuest(current, quest);
            return MapEfficiencyEstimator.EffectiveDensity(estimate) > 0;
        }
        catch { return false; }
    }

    private static bool IsPreferredReachableKillQuest(Quest quest, BaseMap target, double density)
    {
        // Rage kills are map-reachable but consume pills, three staffs, and cross-map supply time. They cannot share the
        // unconditional keep policy of ordinary specified kills; low-progress Dailies use the high-opportunity-cost rule.
        if (quest == null || quest.GetKind() is QuestKind.WindDashKills or QuestKind.KillEnemiesWithRageMode ||
            !NeedsEnemyMap(quest.GetKind()) ||
            target == null || density <= 0) return false;
        try { return !TargetCannotBeKilledWithArrows(quest); }
        catch { return false; }
    }

    private static int Difficulty(double expectedSeconds, QuestSource source)
    {
        var scale = source == QuestSource.Weekly ? 18.0 : 6.0;
        return (int)Math.Clamp(Math.Round(expectedSeconds / scale), 0, 100);
    }

    private static string FormatDuration(double seconds)
    {
        if (double.IsInfinity(seconds) || seconds >= 86400) return "not reachable by current automation";
        return seconds >= 120 ? $"{seconds / 60:0.#}m" : $"{seconds:0}s";
    }

    private static string DisplayName(Quest quest) =>
        string.IsNullOrWhiteSpace(quest?.localizedName) ? quest?.name ?? "Unknown Quest" : quest.localizedName;

}

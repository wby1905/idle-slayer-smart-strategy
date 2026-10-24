using Il2Cpp;
using UnityEngine;

namespace SmartStrategy;

public sealed partial class StrategyBehaviour
{
    private void HandleKeys()
    {
        if (Input.GetKeyDown(ParseKey(Plugin.Config.EmergencyStopKey.Value, KeyCode.F10)))
        {
            _emergencyStopped = true;
            SmartLog.Warning(SmartLogCategory.Core,
                "Emergency stop engaged. Crafting, Rage activation, and automated Portal travel are paused; use the master toggle to recover.");
        }

        if (SmartSettingsUi.IsOpen) return;

        if (Input.GetKeyDown(ParseKey(Plugin.Config.ToggleKey.Value, KeyCode.F9)))
        {
            if (_emergencyStopped)
            {
                _emergencyStopped = false;
                Plugin.Config.Enabled.Value = true;
                SmartLog.Info(SmartLogCategory.Core, "Recovered from emergency stop and enabled Smart Strategy.");
            }
            else
            {
                Plugin.Config.Enabled.Value = !Plugin.Config.Enabled.Value;
                SmartLog.Info(SmartLogCategory.Core,
                    $"Master automation enabled={Plugin.Config.Enabled.Value}.");
            }
        }

        if (Input.GetKeyDown(ParseKey(Plugin.Config.MaterialTravelToggleKey.Value, KeyCode.F6)))
        {
            Plugin.Config.AutomaticMaterialTravel.Value = !Plugin.Config.AutomaticMaterialTravel.Value;
            InvalidateMaterialPlan();
            SmartLog.Info(SmartLogCategory.Material,
                $"Automatic material travel enabled={Plugin.Config.AutomaticMaterialTravel.Value}.");
        }

        if (Input.GetKeyDown(ParseKey(Plugin.Config.QuestToggleKey.Value, KeyCode.F5)))
        {
            Plugin.Config.QuestAutomationEnabled.Value = !Plugin.Config.QuestAutomationEnabled.Value;
            InvalidateMaterialPlan();
            if (!Plugin.Config.QuestAutomationEnabled.Value)
            {
                _quests?.ReleasePlan("quest automation disabled by the player");
                _questPlan = null;
            }
            SmartLog.Info(SmartLogCategory.Quest,
                $"Quest automation enabled={Plugin.Config.QuestAutomationEnabled.Value}.");
        }

        if (Input.GetKeyDown(ParseKey(Plugin.Config.CombatToggleKey.Value, KeyCode.F4)))
        {
            Plugin.Config.AutomaticCombatEnabled.Value = !Plugin.Config.AutomaticCombatEnabled.Value;
            ResetSmartCombat();
            _quests?.ReleasePlan("smart-combat capability changed");
            _questPlan = null;
            InvalidateMaterialPlan();
            SmartLog.Info(SmartLogCategory.Combat,
                $"Movement and smart combat enabled={Plugin.Config.AutomaticCombatEnabled.Value}; quest reachability will be reevaluated.");
        }

        if (Input.GetKeyDown(ParseKey(Plugin.Config.StatusKey.Value, KeyCode.F7))) LogStatus();
    }

    internal void OnSettingsApplied(IReadOnlyCollection<SmartSettingId> changedKeys)
    {
        if (changedKeys == null || changedKeys.Count == 0) return;
        ResetGiantRapidAttack();
        ResetSmartCombat(clearCache: true);
        _quests?.ReleasePlan("in-game settings applied");
        _questPlan = null;
        _parallelQuestPlans = Array.Empty<QuestPlan>();
        _parallelAppliedPlanIds.Clear();
        _materialNeed = null;
        InvalidateMaterialPlan();
        _nextTick = 0f;
        _nextIdleSoulMapScan = 0f;
        _nextSupplementalCraftCheckAt = 0f;
        _nextSupplementalRefineAt = 0f;
        _supplementalRefining.Invalidate();
        _nextPermanentTimedCraftableScanAt = 0f;
        SmartLog.Info(SmartLogCategory.Settings,
            $"Applied {changedKeys.Count} settings and invalidated quest, material, travel, and combat plans.");
    }

    private void LogStatus()
    {
        var soul = ReadSoulsEvent();
        var map = MapController.instance?.selectedMap;
        SmartLog.Info(SmartLogCategory.Core,
            $"Status: enabled={Plugin.Config.Enabled.Value}, endgame={Plugin.EndgameModeActive}, emergency stop={_emergencyStopped}, " +
            $"state={GameState.current}, map={map?.name ?? "unknown"}, Souls event={(soul.Active ? $"{soul.Multiplier:0.#}x/{soul.SecondsLeft:0.0}s" : "none")}, " +
            $"material travel={Plugin.Config.AutomaticMaterialTravel.Value}, zero-cooldown Portal={Plugin.Config.ResetPortalCooldown.Value}, " +
            $"quest automation={Plugin.Config.QuestAutomationEnabled.Value}, rapid Giant attack={Plugin.Config.GiantRapidAttackEnabled.Value}.");
        SmartLog.Info(SmartLogCategory.Combat, CombatStatusSummary());
        SmartLog.Info(SmartLogCategory.Endgame,
            $"Managed client effects: random multipliers={PermanentClientBonuses.ManagedRandomEventCount}, " +
            $"timed craftables={PermanentClientBonuses.ManagedTimedCraftableCount}.");
        SmartLog.Info(SmartLogCategory.Endgame, PaidSoulsBurstStatusSummary());
        SmartLog.Info(SmartLogCategory.Material, "Refinery: " + _supplementalRefining.Status);

        var giantFactor = MapEfficiencyEstimator.CurrentGiantSpawnFactor(includeTemporaryShards: true);
        SmartLog.Info(SmartLogCategory.Planning,
            $"Giant estimate: base interval={GiantKillTimingBridge.BaseIntervalSeconds:0.0}s, factor={giantFactor:0.###}x, " +
            $"expected interval={GiantKillTimingBridge.BaseIntervalSeconds / giantFactor:0.0}s, samples={GiantKillTimingBridge.Samples}.");
        foreach (SmartItem kind in Enum.GetValues(typeof(SmartItem)))
        {
            var item = _catalog.Get(kind);
            if (item == null) continue;
            try
            {
                SmartLog.Info(SmartLogCategory.Material,
                    $"Craftable {kind}: effect={item.currentTime:0.0}s, craftable uses={item.HowManyCanCraft():0}, " +
                    $"tab visible={item.TabVisible()}, extra condition={item.ExtraCondition()}.");
            }
            catch { /* Status output must not affect strategy execution. */ }
        }

        if (_materialNeed != null)
            SmartLog.Info(SmartLogCategory.Material,
                $"Active material plan: current map={map?.name ?? "unknown"}, target map={_materialNeed.Map?.name ?? "unknown"}, " +
                $"material={_materialNeed.Material?.name ?? "unknown"}, amount={_materialNeed.Current:0}/{_materialNeed.Desired:0}; " +
                $"quest map={_questPlan?.TargetMap?.name ?? "any"} is deferred.");
        else if (_questPlan != null)
            SmartLog.Info(SmartLogCategory.Quest,
                $"Active quest plan: current map={map?.name ?? "unknown"}, target map={_questPlan.TargetMap?.name ?? "any"}.");

        if (_questPlan != null)
            SmartLog.Info(SmartLogCategory.Quest,
                $"Quest target: source={_questPlan.Source}, instanceId={_questPlan.InstanceId}, progress={_questPlan.Current}/{_questPlan.Goal}, " +
                $"type={_questPlan.Type}, map={_questPlan.TargetMap?.name ?? "current"}, expected={_questPlan.ExpectedSeconds / 60:0.#}m, " +
                $"density={_questPlan.TargetEnemiesPerPattern:0.##}/pattern, utility={_questPlan.MapUtility:0.###}, " +
                $"joint quests={_questPlan.JointQuestCount}, unlock bonus={_questPlan.StrategicUnlockBonus}.");

        SmartLog.Info(SmartLogCategory.Quest, _quests?.RuntimeCalibrationSummary() ?? "No runtime calibration samples.");
        SmartLog.Info(SmartLogCategory.Quest, _quests?.DailyRerollThresholdSummary() ?? "Daily reroll threshold is not initialized.");
        SmartLog.Info(SmartLogCategory.Quest, QuestRerollEligibilitySummary());
        SmartLog.Info(SmartLogCategory.Quest,
            $"Session rerolls: Daily={_quests?.DailyRerolls ?? 0}, Daily since claim={_quests?.DailyRerollsSinceClaim ?? 0}, " +
            $"Weekly={_quests?.WeeklyRerolls ?? 0}; unavailable skipped: Daily={_quests?.UnavailableDailyCount ?? 0}, " +
            $"Weekly={_quests?.UnavailableWeeklyCount ?? 0}; quest Rage Pills={_ragePillsThisQuest}/{Math.Max(0, Plugin.Config.MaximumRagePillsPerQuestSession.Value)}.");
    }
}

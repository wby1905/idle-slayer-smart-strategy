using Il2Cpp;
using MelonLoader.Utils;

namespace SmartStrategy;

public sealed partial class StrategyBehaviour
{
    private int _ownedDailyRerollId;
    private int _ownedWeeklyRerollId;
    private bool _dailyRerollOriginallyEnabled;
    private bool _weeklyRerollOriginallyEnabled;
    private bool _questAutomationConflictChecked;
    private bool _autoProgressionQuestConflictLogged;

    private void MaintainQuestRerollEligibility()
    {
        var questAutomationActive = Plugin.Config.Enabled.Value && !_emergencyStopped &&
                                    Plugin.Config.QuestAutomationEnabled.Value;
        var dailyActive = questAutomationActive && Plugin.Config.AutoRerollDailyQuests.Value;
        var weeklyActive = questAutomationActive && Plugin.Config.AutoRerollWeeklyQuests.Value;

        MaintainDailyRerollEligibility(dailyActive);
        MaintainWeeklyRerollEligibility(weeklyActive);
        CheckAutoProgressionQuestConflict(dailyActive || weeklyActive);
    }

    private void MaintainDailyRerollEligibility(bool enabled)
    {
        if (!enabled)
        {
            ReleaseDailyRerollEligibility();
            return;
        }

        try
        {
            var reroll = DailyQuestReroll.instance;
            if (reroll == null) return;
            var id = reroll.GetInstanceID();
            if (_ownedDailyRerollId != id)
            {
                _ownedDailyRerollId = id;
                _dailyRerollOriginallyEnabled = reroll.rerollEnabled;
            }
            if (!reroll.rerollEnabled) reroll.rerollEnabled = true;
        }
        catch
        {
            // The native reroll object may be replaced during this frame; resolve the current instance next frame.
            _ownedDailyRerollId = 0;
        }
    }

    private void MaintainWeeklyRerollEligibility(bool enabled)
    {
        if (!enabled)
        {
            ReleaseWeeklyRerollEligibility();
            return;
        }

        try
        {
            var reroll = WeeklyQuestReroll.instance;
            if (reroll == null) return;
            var id = reroll.GetInstanceID();
            if (_ownedWeeklyRerollId != id)
            {
                _ownedWeeklyRerollId = id;
                _weeklyRerollOriginallyEnabled = reroll.rerollEnabled;
            }
            if (!reroll.rerollEnabled) reroll.rerollEnabled = true;
        }
        catch
        {
            // The native reroll object may be replaced during this frame; resolve the current instance next frame.
            _ownedWeeklyRerollId = 0;
        }
    }

    private void ReleaseQuestRerollEligibility()
    {
        ReleaseDailyRerollEligibility();
        ReleaseWeeklyRerollEligibility();
    }

    private void ReleaseDailyRerollEligibility()
    {
        if (_ownedDailyRerollId == 0) return;
        try
        {
            var reroll = DailyQuestReroll.instance;
            if (reroll != null && reroll.GetInstanceID() == _ownedDailyRerollId &&
                !_dailyRerollOriginallyEnabled)
                reroll.rerollEnabled = false;
        }
        catch { /* A stale instance does not need restoration. */ }
        _ownedDailyRerollId = 0;
        _dailyRerollOriginallyEnabled = false;
    }

    private void ReleaseWeeklyRerollEligibility()
    {
        if (_ownedWeeklyRerollId == 0) return;
        try
        {
            var reroll = WeeklyQuestReroll.instance;
            if (reroll != null && reroll.GetInstanceID() == _ownedWeeklyRerollId &&
                !_weeklyRerollOriginallyEnabled)
                reroll.rerollEnabled = false;
        }
        catch { /* A stale instance does not need restoration. */ }
        _ownedWeeklyRerollId = 0;
        _weeklyRerollOriginallyEnabled = false;
    }

    private void CheckAutoProgressionQuestConflict(bool smartRerollActive)
    {
        if (!smartRerollActive)
        {
            return;
        }
        if (_questAutomationConflictChecked) return;
        _questAutomationConflictChecked = true;

        var conflict = AutoProgressionQuestAutomationEnabled();
        if (conflict && !_autoProgressionQuestConflictLogged)
        {
            _autoProgressionQuestConflictLogged = true;
            SmartLog.Warning(SmartLogCategory.Compatibility,
                "AutoProgression still has quest claiming, generation, unlimited rerolls, or filtering enabled. " +
                "Smart Strategy now owns quest behavior and Portal cooldown resets; disable AutoProgression [Quests] Enabled to avoid competing controllers.");
            Plugin.ModHelperInstance?.ShowNotification(
                SmartSettingsLocalization.Text(SmartSettingsText.AutoProgressionQuestConflict), false);
        }
        else if (!conflict)
        {
            _autoProgressionQuestConflictLogged = false;
        }
    }

    private static bool AutoProgressionQuestAutomationEnabled()
    {
        try
        {
            var path = Path.Combine(MelonEnvironment.UserDataDirectory,
                AutoProgressionContract.ConfigFileName);
            if (!File.Exists(path)) return false;
            var inQuestSection = false;
            var questMasterEnabled = false;
            var taskMutationEnabled = false;
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    inQuestSection = line.Equals(AutoProgressionContract.QuestSectionHeader,
                        StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inQuestSection || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var key = line[..separator].Trim().Trim('"');
                if (!bool.TryParse(line[(separator + 1)..].Trim(), out var enabled)) continue;
                if (key.Equals(AutoProgressionContract.QuestMasterEnabledKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    questMasterEnabled = enabled;
                    continue;
                }
                if (AutoProgressionContract.IsQuestMutationKey(key))
                    taskMutationEnabled |= enabled;
            }
            return questMasterEnabled && taskMutationEnabled;
        }
        catch { /* Conflict reporting must not affect reroll behavior. */ }
        return false;
    }

    private string QuestRerollEligibilitySummary()
    {
        var daily = Plugin.Config.Enabled.Value && !_emergencyStopped &&
                    Plugin.Config.QuestAutomationEnabled.Value && Plugin.Config.AutoRerollDailyQuests.Value;
        var weekly = Plugin.Config.Enabled.Value && !_emergencyStopped &&
                     Plugin.Config.QuestAutomationEnabled.Value && Plugin.Config.AutoRerollWeeklyQuests.Value;
        return $"Unlimited reroll ownership: Daily={(daily ? "Smart Strategy" : "not owned")}, " +
               $"Weekly={(weekly ? "Smart Strategy" : "not owned")}";
    }
}

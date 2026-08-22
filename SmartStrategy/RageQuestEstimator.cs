using Il2Cpp;

namespace SmartStrategy;

internal readonly record struct RageQuestTimedBonus(double Bonus, double DurationSeconds);

internal readonly record struct RageQuestRuntime(
    double ActiveSecondsRemaining,
    double CurrentCooldownSeconds,
    double FullCooldownSeconds,
    double RageDurationSeconds,
    int UsablePills);

internal readonly record struct RageQuestEstimate(
    double Seconds,
    double KillActions,
    int RageWindows,
    int PillsUsed);

/// <summary>
/// Models rage-only kill progress on the wall clock. Staff durations continue to elapse between rage
/// windows, while quest progress only advances inside a rage window. Pills are spent only when they
/// remove a real cooldown gap.
/// </summary>
internal static class RageQuestEstimator
{
    internal static RageQuestRuntime Capture(CraftableCatalog catalog, int questPlanId,
        double preparationSeconds = 0, int suppliedPills = 0)
    {
        var duration = 20.0;
        var currentCooldown = 0.0;
        var fullCooldown = 600.0;
        var activeRemaining = 0.0;
        try
        {
            var rage = RageModeManager.instance;
            if (rage != null)
            {
                duration = Math.Max(1, rage.GetDuration());
                currentCooldown = Math.Max(0, rage.currentCd);
                fullCooldown = Math.Max(duration, rage.GetCD());
                if (rage.currentState != RageModeManager.RageModeStates.NotActive)
                    activeRemaining = StrategyBehaviour.Current?.EstimatedRageSecondsRemaining ?? duration;
            }
        }
        catch { /* Keep conservative runtime defaults during scene initialization. */ }

        var allowance = StrategyBehaviour.Current?.RemainingQuestRagePillAllowance(questPlanId) ??
                        Math.Max(0, Plugin.Config.MaximumRagePillsPerQuestSession.Value);
        var existingPills = 0;
        try
        {
            var pill = catalog?.Get(SmartItem.RagePill);
            if (pill != null && pill.TabVisible() && pill.ExtraCondition())
            {
                var craftable = pill.HowManyCanCraft();
                if (double.IsFinite(craftable))
                    existingPills = Math.Max(0, (int)Math.Floor(craftable) -
                                                Math.Max(0, Plugin.Config.RagePillReserveUses.Value));
            }
        }
        catch { existingPills = 0; }

        var delay = Math.Max(0, preparationSeconds);
        activeRemaining = Math.Max(0, activeRemaining - delay);
        currentCooldown = Math.Max(0, currentCooldown - delay);
        var usablePills = Math.Min(Math.Max(0, allowance),
            Math.Max(0, existingPills + Math.Max(0, suppliedPills)));
        return new RageQuestRuntime(activeRemaining, currentCooldown, fullCooldown, duration, usablePills);
    }

    internal static RageQuestEstimate EstimateKill(double remainingProgress, double killsPerMinute,
        double baseCredit, RageQuestRuntime runtime, IReadOnlyList<RageQuestTimedBonus> timedBonuses = null)
    {
        var progressLeft = Math.Max(0, remainingProgress);
        if (progressLeft <= 0) return new RageQuestEstimate(0, 0, 0, 0);

        var killsPerSecond = Math.Max(0.0001, killsPerMinute / 60.0);
        var rageDuration = Math.Max(1, runtime.RageDurationSeconds);
        var fullCooldown = Math.Max(rageDuration, runtime.FullCooldownSeconds);
        var currentCooldown = Math.Max(0, runtime.CurrentCooldownSeconds);
        var pillsLeft = Math.Max(0, runtime.UsablePills);
        var pillsUsed = 0;
        var windows = 0;
        var killActions = 0.0;

        double windowStart;
        double windowDuration;
        double naturalReadyAt;
        if (runtime.ActiveSecondsRemaining > 0.01)
        {
            windowStart = 0;
            windowDuration = runtime.ActiveSecondsRemaining;
            naturalReadyAt = currentCooldown;
        }
        else if (currentCooldown <= 0.01)
        {
            windowStart = 0;
            windowDuration = rageDuration;
            naturalReadyAt = fullCooldown;
        }
        else if (pillsLeft > 0)
        {
            pillsLeft--;
            pillsUsed++;
            windowStart = 0;
            windowDuration = rageDuration;
            naturalReadyAt = fullCooldown;
        }
        else
        {
            windowStart = currentCooldown;
            windowDuration = rageDuration;
            naturalReadyAt = windowStart + fullCooldown;
        }

        // Task staffs are crafted immediately before the first usable rage window.
        var bonusStart = windowStart;
        for (var guard = 0; guard < 10000; guard++)
        {
            windows++;
            var cursor = windowStart;
            var windowEnd = windowStart + Math.Max(0.01, windowDuration);
            while (cursor < windowEnd - 0.0001)
            {
                var sinceBonusStart = Math.Max(0, cursor - bonusStart);
                var bonus = 0.0;
                var segmentEnd = windowEnd;
                if (timedBonuses != null)
                {
                    for (var i = 0; i < timedBonuses.Count; i++)
                    {
                        var effect = timedBonuses[i];
                        if (effect.Bonus <= 0 || effect.DurationSeconds <= sinceBonusStart + 0.0001) continue;
                        bonus += effect.Bonus;
                        segmentEnd = Math.Min(segmentEnd, bonusStart + effect.DurationSeconds);
                    }
                }

                var credit = Math.Max(1, baseCredit + bonus);
                var progressPerSecond = killsPerSecond * credit;
                var availableSeconds = Math.Max(0.0001, segmentEnd - cursor);
                var possibleProgress = progressPerSecond * availableSeconds;
                if (possibleProgress >= progressLeft)
                {
                    var neededSeconds = progressLeft / progressPerSecond;
                    killActions += killsPerSecond * neededSeconds;
                    return new RageQuestEstimate(cursor + neededSeconds, killActions, windows, pillsUsed);
                }

                progressLeft -= possibleProgress;
                killActions += killsPerSecond * availableSeconds;
                cursor = segmentEnd;
            }

            if (naturalReadyAt <= windowEnd + 0.0001)
            {
                windowStart = windowEnd;
                naturalReadyAt = windowStart + fullCooldown;
            }
            else if (pillsLeft > 0)
            {
                pillsLeft--;
                pillsUsed++;
                windowStart = windowEnd;
                naturalReadyAt = windowStart + fullCooldown;
            }
            else
            {
                windowStart = naturalReadyAt;
                naturalReadyAt = windowStart + fullCooldown;
            }
            windowDuration = rageDuration;
        }

        return new RageQuestEstimate(double.PositiveInfinity, double.PositiveInfinity, windows, pillsUsed);
    }
}

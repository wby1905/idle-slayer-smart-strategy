namespace SmartStrategy;

/// <param name="RemainingSeconds">Time until the entire unfinished queue completes.</param>
/// <param name="RemainingOutputs">Outputs still processing; excludes claimable outputs.</param>
/// <param name="ClaimableOutputs">Completed outputs that have not been collected.</param>
/// <param name="IsOwned">Whether this mod created the queue. Forecasts preserve all queues.</param>
internal readonly record struct RefineryForecastSlot(int Index, string OutputKey,
    double RemainingSeconds, int RemainingOutputs, int ClaimableOutputs, bool IsOwned = false,
    bool CanAcceptNewWork = true);

/// <param name="MissingOutputs">Deficit after stored inventory, but before unclaimed/queued output.</param>
/// <param name="Priority">Lower values are scheduled first among ready materials.</param>
/// <param name="MaximumNewOutputs">Raw-funded output limit, excluding existing queues.</param>
/// <param name="InputReadySeconds">Earliest new-work start; infinity means unavailable.</param>
internal readonly record struct RefineryForecastDemand(string OutputKey, int MissingOutputs,
    int Priority = 0, int MaximumNewOutputs = int.MaxValue, double InputReadySeconds = 0);

internal readonly record struct RefinerySpendEstimate(double BeforeSeconds, double AfterSeconds,
    double SavedSeconds, double SecondsPerJewel, bool Eligible, bool AlreadyMeetsDeadline,
    bool CanMeetDeadline);

/// <summary>
/// Uses the same deterministic, bounded batch schedule for execution and paid-action forecasts.
/// Capacity, raw inventory and native purchase permissions remain the caller's responsibility.
/// </summary>
internal static class RefineryForecast
{
    private const double TimeEpsilon = 0.000001;

    internal static RefinerySchedule Plan(IReadOnlyList<RefineryForecastSlot> slots,
        IReadOnlyList<RefineryForecastDemand> demands, int unlockedSlots, double cycleSeconds,
        int maxBatchOutputs = 6) =>
        RefinerySchedulePlanner.Build(slots, demands, unlockedSlots, cycleSeconds, maxBatchOutputs);

    internal static double EstimateMaterialCompletionSeconds(IReadOnlyList<RefineryForecastSlot> slots,
        string outputKey, int missingOutputs, int unlockedSlots, double cycleSeconds,
        int maxBatchOutputs = 6) =>
        EstimateCompletionSeconds(slots,
            new[] { new RefineryForecastDemand(outputKey, missingOutputs) }, unlockedSlots, cycleSeconds,
            maxBatchOutputs);

    /// <summary>
    /// Returns the shared schedule's completion time, zero for stocked demands, or positive
    /// infinity for invalid/unfunded snapshots or plans exceeding the explicit simulation limits.
    /// </summary>
    internal static double EstimateCompletionSeconds(IReadOnlyList<RefineryForecastSlot> slots,
        IReadOnlyList<RefineryForecastDemand> demands, int unlockedSlots, double cycleSeconds,
        int maxBatchOutputs = 6) =>
        Plan(slots, demands, unlockedSlots, cycleSeconds, maxBatchOutputs).CompletionSeconds;

    internal static RefinerySpendEstimate EvaluateSpeedup(IReadOnlyList<RefineryForecastSlot> slots,
        IReadOnlyList<RefineryForecastDemand> demands, int unlockedSlots, double cycleSeconds,
        int slotIndex, double jewelCost, double minimumSecondsPerJewel,
        double minimumSecondsSaved = 0, double? deadlineSeconds = null, int maxBatchOutputs = 6)
    {
        var beforePlan = Plan(slots, demands, unlockedSlots, cycleSeconds, maxBatchOutputs);
        var before = beforePlan.CompletionSeconds;
        if (!beforePlan.IsValid || slots == null)
            return Improvement(before, before, jewelCost, minimumSecondsPerJewel,
                minimumSecondsSaved, deadlineSeconds, false);

        var accelerated = slots.ToArray();
        var found = false;
        for (var i = 0; i < accelerated.Length; i++)
        {
            var slot = accelerated[i];
            if (slot.Index != slotIndex) continue;
            if (found || slot.RemainingOutputs <= 0 || !double.IsFinite(slot.RemainingSeconds) ||
                slot.RemainingSeconds <= 0 || slot.ClaimableOutputs < 0 ||
                (long)slot.ClaimableOutputs + slot.RemainingOutputs > int.MaxValue)
                return Improvement(before, before, jewelCost, minimumSecondsPerJewel,
                    minimumSecondsSaved, deadlineSeconds, false);

            // Native acceleration finishes the whole queue. Move unfinished outputs to
            // claimable stock rather than modelling a zero-time queue with future output.
            accelerated[i] = slot with
            {
                RemainingSeconds = 0,
                RemainingOutputs = 0,
                ClaimableOutputs = slot.ClaimableOutputs + slot.RemainingOutputs
            };
            found = true;
        }

        var after = found
            ? EstimateCompletionSeconds(accelerated, demands, unlockedSlots, cycleSeconds, maxBatchOutputs)
            : before;
        return Improvement(before, after, jewelCost, minimumSecondsPerJewel,
            minimumSecondsSaved, deadlineSeconds, found);
    }

    internal static RefinerySpendEstimate EvaluateNewSlot(IReadOnlyList<RefineryForecastSlot> slots,
        IReadOnlyList<RefineryForecastDemand> demands, int unlockedSlots, double cycleSeconds,
        double jewelCost, double minimumSecondsPerJewel, double minimumSecondsSaved = 0,
        double? deadlineSeconds = null, int maxBatchOutputs = 6)
    {
        var beforePlan = Plan(slots, demands, unlockedSlots, cycleSeconds, maxBatchOutputs);
        var before = beforePlan.CompletionSeconds;
        var valid = beforePlan.IsValid && unlockedSlots >= 0 && unlockedSlots < int.MaxValue;
        // A missing next slot is idle. If a snapshot already contains that slot, its fixed queue
        // is preserved. The caller checks the native maximum slot count before purchasing.
        var after = valid
            ? EstimateCompletionSeconds(slots, demands, unlockedSlots + 1, cycleSeconds, maxBatchOutputs)
            : before;
        return Improvement(before, after, jewelCost, minimumSecondsPerJewel,
            minimumSecondsSaved, deadlineSeconds, valid);
    }

    private static RefinerySpendEstimate Improvement(double before, double after, double cost,
        double minimumSecondsPerJewel, double minimumSecondsSaved, double? deadline, bool validCandidate)
    {
        var finiteAfter = double.IsFinite(after);
        var saved = !finiteAfter ? 0 : double.IsPositiveInfinity(before)
            ? double.PositiveInfinity : Math.Max(0, before - after);
        var validCost = double.IsFinite(cost) && cost >= 0;
        var secondsPerJewel = !validCost || saved <= 0 ? 0 : cost > 0 ? saved / cost : double.PositiveInfinity;
        var validDeadline = !deadline.HasValue || double.IsFinite(deadline.Value) && deadline.Value >= 0;
        var alreadyMeetsDeadline = validDeadline && deadline.HasValue && double.IsFinite(before) &&
                                   before <= deadline.Value + TimeEpsilon;
        var canMeetDeadline = validDeadline && deadline.HasValue && finiteAfter &&
                              after <= deadline.Value + TimeEpsilon;
        var validThresholds = double.IsFinite(minimumSecondsPerJewel) && minimumSecondsPerJewel >= 0 &&
                              double.IsFinite(minimumSecondsSaved) && minimumSecondsSaved >= 0;
        var eligible = validCandidate && validCost && validDeadline && validThresholds && finiteAfter &&
                       before > TimeEpsilon && !alreadyMeetsDeadline &&
                       saved > TimeEpsilon && saved + TimeEpsilon >= minimumSecondsSaved &&
                       secondsPerJewel >= minimumSecondsPerJewel;
        return new RefinerySpendEstimate(before, after, saved, secondsPerJewel, eligible,
            alreadyMeetsDeadline, canMeetDeadline);
    }
}

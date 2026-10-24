namespace SmartStrategy;

/// <param name="RemainingSeconds">Time until the entire unfinished queue completes.</param>
/// <param name="RemainingOutputs">Outputs still processing; excludes claimable outputs.</param>
/// <param name="ClaimableOutputs">Completed outputs that have not been collected.</param>
/// <param name="IsOwned">Whether this mod created the queue. Forecasts preserve all queues.</param>
internal readonly record struct RefineryForecastSlot(int Index, string OutputKey,
    double RemainingSeconds, int RemainingOutputs, int ClaimableOutputs, bool IsOwned = false);

/// <param name="MissingOutputs">Deficit after stored inventory, but before unclaimed/queued output.</param>
internal readonly record struct RefineryForecastDemand(string OutputKey, int MissingOutputs);

internal readonly record struct RefinerySpendEstimate(double BeforeSeconds, double AfterSeconds,
    double SavedSeconds, double SecondsPerJewel, bool Eligible, bool AlreadyMeetsDeadline,
    bool CanMeetDeadline);

/// <summary>
/// Forecasts fixed existing queues and additional equal-duration refinery cycles. Existing jobs
/// are never cancelled, including manual queues and queues beyond the current unlock count.
/// New jobs cover only deficits not already committed in existing queues; pending output is
/// never duplicated just to finish sooner. The caller must clip demands to available raw inputs and output capacity, and ensure that
/// completed output can be collected. Travel, polling and resource acquisition delays are excluded.
/// </summary>
internal static class RefineryForecast
{
    private const int SearchIterations = 64;
    private const double TimeEpsilon = 0.000001;

    internal static double EstimateMaterialCompletionSeconds(IReadOnlyList<RefineryForecastSlot> slots,
        string outputKey, int missingOutputs, int unlockedSlots, double cycleSeconds) =>
        EstimateCompletionSeconds(slots,
            new[] { new RefineryForecastDemand(outputKey, missingOutputs) }, unlockedSlots, cycleSeconds);

    /// <summary>
    /// Returns the earliest time at which all demands can be supplied. Returns zero for stocked
    /// demands and positive infinity for invalid snapshots or demands with no available production.
    /// Work is bounded by snapshot size and 64 search steps, not queue size or requested quantity.
    /// </summary>
    internal static double EstimateCompletionSeconds(IReadOnlyList<RefineryForecastSlot> slots,
        IReadOnlyList<RefineryForecastDemand> demands, int unlockedSlots, double cycleSeconds)
    {
        if (demands == null || !demands.Any(demand => demand.MissingOutputs > 0)) return 0;
        if (unlockedSlots < 0 || !double.IsFinite(cycleSeconds) || cycleSeconds <= 0)
            return double.PositiveInfinity;

        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var missing = new List<long>();
        foreach (var demand in demands)
        {
            if (demand.MissingOutputs <= 0) continue;
            if (string.IsNullOrWhiteSpace(demand.OutputKey)) return double.PositiveInfinity;
            if (indices.TryGetValue(demand.OutputKey, out var index))
                missing[index] += demand.MissingOutputs;
            else
            {
                indices.Add(demand.OutputKey, missing.Count);
                missing.Add(demand.MissingOutputs);
            }
        }

        var queues = new List<Queue>();
        var usedIndices = new HashSet<int>();
        var emptySlots = unlockedSlots;
        var lastCompletion = 0d;
        if (slots != null)
        {
            foreach (var slot in slots)
            {
                if (slot.Index < 0 || !usedIndices.Add(slot.Index) ||
                    slot.RemainingOutputs < 0 || slot.ClaimableOutputs < 0 ||
                    !double.IsFinite(slot.RemainingSeconds) || slot.RemainingSeconds < 0)
                    return double.PositiveInfinity;

                var materialIndex = slot.OutputKey != null && indices.TryGetValue(slot.OutputKey, out var i)
                    ? i : -1;
                var release = slot.RemainingOutputs > 0 ? slot.RemainingSeconds : 0;
                if (materialIndex >= 0)
                    missing[materialIndex] = Math.Max(0, missing[materialIndex] - slot.ClaimableOutputs);

                // Slots omitted from a snapshot are idle. An observed queue outside the current
                // unlock count may supply its output, but cannot accept new work after finishing.
                var canAcceptWork = slot.Index < unlockedSlots;
                if (canAcceptWork) emptySlots--;
                queues.Add(new Queue(materialIndex, release, slot.RemainingOutputs, canAcceptWork));
                if (canAcceptWork || materialIndex >= 0) lastCompletion = Math.Max(lastCompletion, release);
            }
        }

        var needs = missing.ToArray();
        var pending = new long[needs.Length];
        foreach (var queue in queues)
            if (queue.MaterialIndex >= 0) pending[queue.MaterialIndex] += queue.RemainingOutputs;
        var maximumNew = new long[needs.Length];
        for (var index = 0; index < needs.Length; index++)
            maximumNew[index] = Math.Max(0, needs[index] - pending[index]);
        var scratch = new long[needs.Length];
        if (CompleteBy(0)) return 0;

        // Waiting for all fixed queues and then distributing remaining demand across owned slots
        // gives a finite upper bound. Without owned slots, only fixed output can satisfy demand.
        var totalNeeded = needs.Sum();
        var high = lastCompletion;
        if (unlockedSlots > 0)
            high += Math.Ceiling((double)totalNeeded / unlockedSlots) * cycleSeconds;
        if (!double.IsFinite(high) || !CompleteBy(high)) return double.PositiveInfinity;

        var low = 0d;
        for (var step = 0; step < SearchIterations && high - low > TimeEpsilon; step++)
        {
            var middle = low + (high - low) / 2;
            if (CompleteBy(middle)) high = middle;
            else low = middle;
        }
        return high;

        bool CompleteBy(double seconds)
        {
            Array.Copy(needs, scratch, needs.Length);
            foreach (var queue in queues)
            {
                var index = queue.MaterialIndex;
                if (index < 0 || scratch[index] <= 0 || queue.RemainingOutputs <= 0) continue;

                // The last output arrives at ReleaseSeconds; earlier outputs are one cycle
                // apart. Arithmetic truncation handles large manual batches without enumeration.
                var cyclesUntilFinish = Math.Max(0,
                    Math.Ceiling((queue.ReleaseSeconds - seconds - TimeEpsilon) / cycleSeconds));
                var completed = queue.RemainingOutputs - Math.Min(queue.RemainingOutputs, cyclesUntilFinish);
                scratch[index] -= Math.Min(scratch[index], (long)completed);
            }

            // The director never starts replacement work for stock already committed in a
            // fixed queue. Spare slots cannot bypass a slow manual queue using unbudgeted raw
            // ingredients: its promised portion must actually arrive before this deadline.
            for (var index = 0; index < scratch.Length; index++)
                if (scratch[index] > maximumNew[index]) return false;
            var remaining = scratch.Sum();
            if (remaining <= 0) return true;

            var freeCycles = WholeCycles(seconds, remaining);
            if (emptySlots > 0 && freeCycles >= Math.Ceiling((double)remaining / emptySlots)) return true;
            remaining -= emptySlots * freeCycles;
            foreach (var queue in queues)
            {
                if (!queue.CanAcceptWork) continue;
                remaining -= WholeCycles(seconds - queue.ReleaseSeconds, remaining);
                if (remaining <= 0) return true;
            }
            return false;
        }

        long WholeCycles(double seconds, long limit)
        {
            if (seconds < 0 || limit <= 0) return 0;
            var cycles = Math.Floor((seconds + TimeEpsilon) / cycleSeconds);
            return (long)Math.Min(limit, Math.Max(0, cycles));
        }
    }

    internal static RefinerySpendEstimate EvaluateSpeedup(IReadOnlyList<RefineryForecastSlot> slots,
        IReadOnlyList<RefineryForecastDemand> demands, int unlockedSlots, double cycleSeconds,
        int slotIndex, double jewelCost, double minimumSecondsPerJewel,
        double minimumSecondsSaved = 0, double? deadlineSeconds = null)
    {
        var before = EstimateCompletionSeconds(slots, demands, unlockedSlots, cycleSeconds);
        if (slots == null)
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

        var after = found ? EstimateCompletionSeconds(accelerated, demands, unlockedSlots, cycleSeconds) : before;
        return Improvement(before, after, jewelCost, minimumSecondsPerJewel,
            minimumSecondsSaved, deadlineSeconds, found);
    }

    internal static RefinerySpendEstimate EvaluateNewSlot(IReadOnlyList<RefineryForecastSlot> slots,
        IReadOnlyList<RefineryForecastDemand> demands, int unlockedSlots, double cycleSeconds,
        double jewelCost, double minimumSecondsPerJewel, double minimumSecondsSaved = 0,
        double? deadlineSeconds = null)
    {
        var before = EstimateCompletionSeconds(slots, demands, unlockedSlots, cycleSeconds);
        var valid = unlockedSlots >= 0 && unlockedSlots < int.MaxValue;
        // A missing next slot is idle. If a snapshot already contains that slot, its fixed queue
        // is preserved. The caller checks the native maximum slot count before purchasing.
        var after = valid ? EstimateCompletionSeconds(slots, demands, unlockedSlots + 1, cycleSeconds) : before;
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

    private readonly record struct Queue(int MaterialIndex, double ReleaseSeconds, int RemainingOutputs,
        bool CanAcceptWork);
}

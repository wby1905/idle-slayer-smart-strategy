using System.Collections.ObjectModel;

namespace SmartStrategy;

internal readonly record struct RefineryScheduleJob(int SlotIndex, string OutputKey, int Outputs,
    double StartSeconds, double FinishSeconds)
{
    internal int Index => SlotIndex;
}

internal sealed class RefinerySchedule
{
    internal IReadOnlyList<RefineryScheduleJob> Jobs { get; }
    internal IReadOnlyDictionary<string, double> CompletionByOutput { get; }
    internal double CompletionSeconds { get; }
    internal bool IsValid { get; }
    internal bool IsReachable => double.IsFinite(CompletionSeconds);
    internal string FailureReason { get; }

    internal RefinerySchedule(List<RefineryScheduleJob> jobs, Dictionary<string, double> completion,
        double completionSeconds, string failureReason = null, bool isValid = true)
    {
        Jobs = jobs.AsReadOnly();
        CompletionByOutput = new ReadOnlyDictionary<string, double>(completion);
        CompletionSeconds = completionSeconds;
        IsValid = isValid;
        FailureReason = failureReason;
    }
}

/// <summary>
/// Schedules only work not already committed in a fixed queue. Existing output may be collected
/// before its full queue ends, but that slot cannot accept another batch until the queue finishes.
/// Claiming, polling and UI delays are excluded; the caller must ensure output fits inventory.
/// </summary>
internal static class RefinerySchedulePlanner
{
    private const int MaximumSlots = 64;
    private const int MaximumMaterials = 128;
    private const int MaximumJobs = 2048;
    private const int MaximumNewOutputs = 8192;
    private const int MaximumSimulationSteps = 4096;
    private const int CompletionSearchSteps = 64;
    private const double TimeEpsilon = 0.000001;

    internal static RefinerySchedule Build(IReadOnlyList<RefineryForecastSlot> slots,
        IReadOnlyList<RefineryForecastDemand> demands, int unlockedSlots, double cycleSeconds,
        int maxBatchOutputs)
    {
        if (demands?.Count > MaximumMaterials * 4)
            return Invalid("Demand-entry limit exceeded");
        var materials = new Dictionary<string, Material>(StringComparer.Ordinal);
        if (demands != null)
            foreach (var demand in demands)
            {
                if (string.IsNullOrWhiteSpace(demand.OutputKey))
                {
                    if (demand.MissingOutputs > 0) return Invalid("A requested material has no key");
                    continue;
                }
                if (demand.MaximumNewOutputs < 0 || double.IsNaN(demand.InputReadySeconds) ||
                    demand.InputReadySeconds < 0)
                    return Invalid("Invalid input availability", materials.Keys);
                if (!materials.TryGetValue(demand.OutputKey, out var material))
                {
                    if (materials.Count >= MaximumMaterials)
                        return Invalid("Material-count limit exceeded", materials.Keys);
                    material = new Material(demand.OutputKey, demand.Priority,
                        demand.MaximumNewOutputs, demand.InputReadySeconds);
                    materials.Add(demand.OutputKey, material);
                }
                else
                {
                    // Callers should aggregate each material once. Duplicate requests cannot
                    // multiply the same raw inventory or assume that all inputs arrive earlier.
                    material.Priority = Math.Min(material.Priority, demand.Priority);
                    material.MaximumNew = Math.Min(material.MaximumNew, demand.MaximumNewOutputs);
                    material.ReadySeconds = Math.Max(material.ReadySeconds, demand.InputReadySeconds);
                }
                material.Missing += Math.Max(0, demand.MissingOutputs);
            }
        if (materials.Count == 0)
            return new RefinerySchedule(new(), new(StringComparer.Ordinal), 0);
        if (unlockedSlots < 0 || unlockedSlots > MaximumSlots || slots?.Count > MaximumSlots ||
            !double.IsFinite(cycleSeconds) || cycleSeconds <= 0 || maxBatchOutputs <= 0)
            return Invalid("Invalid slot count, cycle duration or batch limit", materials.Keys);

        var freeAt = new double[unlockedSlots];
        var usedIndices = new HashSet<int>();
        if (slots != null)
            foreach (var slot in slots)
            {
                if (slot.Index < 0 || !usedIndices.Add(slot.Index) || slot.RemainingOutputs < 0 ||
                    slot.ClaimableOutputs < 0 || !double.IsFinite(slot.RemainingSeconds) || slot.RemainingSeconds < 0)
                    return Invalid("Invalid or duplicate fixed queue", materials.Keys);
                var release = slot.RemainingOutputs > 0 ? slot.RemainingSeconds : 0;
                if (slot.Index < unlockedSlots)
                    freeAt[slot.Index] = slot.CanAcceptNewWork ? release : double.PositiveInfinity;
                if (slot.OutputKey == null || !materials.TryGetValue(slot.OutputKey, out var material)) continue;
                material.Missing = Math.Max(0, material.Missing - slot.ClaimableOutputs);
                if (slot.RemainingOutputs <= 0) continue;
                material.Pending += slot.RemainingOutputs;
                material.Existing.Add(new Production(release, slot.RemainingOutputs));
            }

        var ordered = materials.Values.OrderBy(material => material.Key, StringComparer.Ordinal).ToArray();
        var newOutputs = 0L;
        foreach (var material in ordered)
        {
            material.RequiredNew = Math.Max(0, material.Missing - material.Pending);
            material.FundedNew = Math.Min(material.RequiredNew, material.MaximumNew);
            material.Unscheduled = material.FundedNew;
            newOutputs += material.FundedNew;
            if (newOutputs > MaximumNewOutputs)
                return Invalid("Planned output limit exceeded", materials.Keys);
        }

        var jobs = new List<RefineryScheduleJob>();
        var now = 0d;
        var steps = 0;
        while (ordered.Any(material => material.Unscheduled > 0) && freeAt.Length > 0)
        {
            if (++steps > MaximumSimulationSteps)
                return Invalid("Simulation step limit exceeded", materials.Keys);

            var firstFree = -1;
            var freeCount = 0;
            for (var slot = 0; slot < freeAt.Length; slot++)
                if (freeAt[slot] <= now)
                {
                    if (firstFree < 0) firstFree = slot;
                    freeCount++;
                }
            Material selected = null;
            foreach (var material in ordered)
            {
                if (material.Unscheduled <= 0 || material.ReadySeconds > now) continue;
                if (selected == null || material.Priority < selected.Priority ||
                    material.Priority == selected.Priority && material.Unscheduled > selected.Unscheduled)
                    selected = material;
            }

            if (firstFree >= 0 && selected != null)
            {
                // Spread the current priority group's work over idle slots. Recompute after
                // each reservation, select the largest outstanding material, then tie-break by
                // its stable key and the lowest slot index. Execution consumes this same list.
                var groupWork = 0L;
                foreach (var material in ordered)
                    if (material.Priority == selected.Priority && material.ReadySeconds <= now)
                        groupWork += material.Unscheduled;
                var balancedBatch = (groupWork + freeCount - 1) / freeCount;
                var count = (int)Math.Min(selected.Unscheduled, Math.Min(maxBatchOutputs, balancedBatch));
                var finish = now + count * cycleSeconds;
                if (count <= 0 || !double.IsFinite(finish) || finish <= now)
                    return Invalid("Unrepresentable batch duration", materials.Keys);
                if (jobs.Count >= MaximumJobs)
                    return Invalid("Scheduled job limit exceeded", materials.Keys);
                jobs.Add(new RefineryScheduleJob(firstFree, selected.Key, count, now, finish));
                freeAt[firstFree] = finish;
                selected.Unscheduled -= count;
                selected.LastNewCompletion = Math.Max(selected.LastNewCompletion, finish);
                continue;
            }

            var next = double.PositiveInfinity;
            foreach (var time in freeAt)
                if (time > now) next = Math.Min(next, time);
            foreach (var material in ordered)
                if (material.Unscheduled > 0 && material.ReadySeconds > now)
                    next = Math.Min(next, material.ReadySeconds);
            if (!double.IsFinite(next)) break;
            now = next;
        }

        var completion = new Dictionary<string, double>(StringComparer.Ordinal);
        var allComplete = 0d;
        foreach (var material in ordered)
        {
            double complete;
            if (material.Missing <= 0) complete = 0;
            else if (material.RequiredNew == 0)
                complete = ExistingCompletion(material.Existing, material.Missing, cycleSeconds);
            else if (material.FundedNew < material.RequiredNew || material.Unscheduled > 0)
                complete = double.PositiveInfinity;
            else
            {
                // If new output is needed, every fixed output and every planned output is
                // necessary. Thus only their final completion times need to be compared.
                complete = material.LastNewCompletion;
                foreach (var production in material.Existing) complete = Math.Max(complete, production.Release);
            }
            completion.Add(material.Key, complete);
            allComplete = Math.Max(allComplete, complete);
        }
        return new RefinerySchedule(jobs, completion, allComplete,
            double.IsFinite(allComplete) ? null : "Some demand lacks funded inputs, an available slot or an input-ready time");
    }

    private static double ExistingCompletion(IReadOnlyList<Production> queues, long needed, double cycleSeconds)
    {
        if (needed <= 0) return 0;
        if (queues.Count == 0) return double.PositiveInfinity;
        if (queues.Count == 1)
        {
            var only = queues[0];
            return needed > only.Outputs ? double.PositiveInfinity
                : Math.Max(0, only.Release - (only.Outputs - needed) * cycleSeconds);
        }
        var high = queues.Max(queue => queue.Release);
        if (!CompleteBy(high)) return double.PositiveInfinity;
        if (CompleteBy(0)) return 0;
        var low = 0d;
        for (var step = 0; step < CompletionSearchSteps && high - low > TimeEpsilon; step++)
        {
            var middle = low + (high - low) / 2;
            if (CompleteBy(middle)) high = middle;
            else low = middle;
        }

        // Snap to a real production boundary instead of returning an arbitrary binary-search
        // midpoint. Only fixed queues are searched, never individual outputs in a large batch.
        var lastOutput = 0d;
        foreach (var queue in queues)
        {
            var completed = CountBy(queue, high);
            if (completed > 0)
                lastOutput = Math.Max(lastOutput,
                    Math.Max(0, queue.Release - (queue.Outputs - completed) * cycleSeconds));
        }
        return lastOutput;

        bool CompleteBy(double seconds)
        {
            var left = needed;
            foreach (var queue in queues)
            {
                left -= CountBy(queue, seconds);
                if (left <= 0) return true;
            }
            return false;
        }

        long CountBy(Production queue, double seconds)
        {
            var unfinished = Math.Max(0, Math.Ceiling((queue.Release - seconds - TimeEpsilon) / cycleSeconds));
            return queue.Outputs - (long)Math.Min(queue.Outputs, unfinished);
        }
    }

    private static RefinerySchedule Invalid(string reason, IEnumerable<string> keys = null)
    {
        var completion = new Dictionary<string, double>(StringComparer.Ordinal);
        if (keys != null)
            foreach (var key in keys) completion[key] = double.PositiveInfinity;
        return new RefinerySchedule(new(), completion, double.PositiveInfinity, reason, isValid: false);
    }

    private readonly record struct Production(double Release, int Outputs);

    private sealed class Material
    {
        internal readonly string Key;
        internal readonly List<Production> Existing = new();
        internal int Priority, MaximumNew;
        internal double ReadySeconds, LastNewCompletion;
        internal long Missing, Pending, RequiredNew, FundedNew, Unscheduled;

        internal Material(string key, int priority, int maximumNew, double readySeconds)
        {
            Key = key;
            Priority = priority;
            MaximumNew = maximumNew;
            ReadySeconds = readySeconds;
        }
    }
}

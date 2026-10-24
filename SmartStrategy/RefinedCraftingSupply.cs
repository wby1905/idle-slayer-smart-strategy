using Il2Cpp;
using UnityEngine;

namespace SmartStrategy;

internal readonly record struct RefineryRecipeRequest(TemporaryCraftableItem Item, int Copies, int Priority,
    bool Immediate = false, double? DeadlineSeconds = null, bool AllowRawFarming = true,
    int QuestPlanId = 0, string Purpose = null, bool AllowPremium = true,
    Quest Task = null, SmartItem? TaskItem = null)
{
    internal const int SurplusPriority = 50;
    internal const int IdlePriority = 100;
    internal bool IsApplicable
    {
        get
        {
            if (QuestPlanId == 0) return true;
            try { return Task != null && Task.GetInstanceID() == QuestPlanId && TaskItem.HasValue &&
                QuestEffectPolicy.CanSpend(Task, TaskItem.Value); }
            catch { return false; }
        }
    }
}

// Uses the native backend, never the Village popup. No timer editing, cancellation or discarding.
internal sealed class RefinedCraftingSupply
{
    private const double Epsilon = 0.0001;
    private const string RefineryBuildingId = "ihda7gqmgppigg8u88lq";
    private readonly CraftableCatalog _safetyCatalog = new();
    internal CraftSupplyPlanner SupplyPlanner { get; } = new();
    private readonly RefineryPremiumPolicy _premium = new();
    private readonly List<Need> _needs = new();
    private readonly Dictionary<string, double> _directRecipeStock = new(StringComparer.Ordinal);
    private readonly Dictionary<int, OwnedQueue> _owned = new();
    private readonly Dictionary<string, double> _pending = new(StringComparer.Ordinal);
    private readonly List<RefineryForecastSlot> _snapshot = new();
    private Func<Drop, double> _externalReserve;
    private float _nextPollAt, _nextLogAt, _nextFailureLogAt, _nextPremiumAt;
    private float _lastSnapshotAt = -100;
    private bool _writesSuspended, _canForecast;
    private string _status = "waiting for the native refinery";
    internal string Status => _status + "; " + _premium.Status;
    internal void ReleaseOwnership() => _owned.Clear();

    internal void Invalidate()
    {
        _nextPollAt = 0;
        _lastSnapshotAt = -100;
        _needs.Clear(); // A settings toggle must not clear ownership or uncertain-write protection.
    }

    internal bool Tick(IReadOnlyList<RefineryRecipeRequest> requests, Func<Drop, double> protectedStock,
        bool allowNewWork)
    {
        if (Plugin.IsApplicationQuitting || !Plugin.Config.SupplementalRefiningEnabled.Value)
        {
            _needs.Clear();
            _status = "disabled; existing native queues continue";
            return false;
        }
        var now = Time.unscaledTime;
        if (now < _nextPollAt) return false;
        _nextPollAt = now + 2f;
        try
        {
            var manager = RefineryManager.instance;
            if (!FeatureUnlocked() || manager?.slots == null)
            {
                _needs.Clear();
                _status = "locked: finish the Village refinery and obtain Book of Refinement from Jack";
                return false;
            }
            if (!Positive(RefineryManager.MATERIALS_PER_OUTPUT) || !Positive(RefineryManager.SECONDS_PER_CYCLE) ||
                RefineryManager.MAX_SLOTS <= 0) return false;
            var usable = Math.Min(manager.slots.Length, Math.Min(RefineryManager.MAX_SLOTS, Math.Max(0, manager.unlockedSlots)));
            if (usable == 0) return false;
            _externalReserve = protectedStock;
            ReadNeeds(manager, requests);
            ReadQueues(manager);
            _lastSnapshotAt = now;
            _status = $"slots={usable}/{RefineryManager.MAX_SLOTS}, recipe shortages=" +
                $"{_needs.Count(n => n.RecipeWanted > n.Output.amount + Pending(n.Output) + Epsilon)}, " +
                $"queued outputs={_pending.Values.Sum():0}";
            _status += "; " + string.Join("; ", _snapshot.Select(s =>
                $"slot {s.Index + 1}: {s.RemainingSeconds / 60:0.#}m, ready={s.ClaimableOutputs}, " +
                $"processing={s.RemainingOutputs}, observed auto queue={s.IsOwned}"));
            if (_writesSuspended) { _status += "; native writes suspended until restart/check"; return false; }

            // Claim even after the original event ends. Native statistics count the full claimable
            // amount on overflow: wait until the entire completed portion fits, never retry zero-fit claims.
            for (var i = 0; i < manager.slots.Length; i++)
            {
                var slot = manager.slots[i];
                var output = slot?.material?.refinedTarget;
                if (output == null || !slot.IsActive || !Stock(output, out var stock, out var capacity)) continue;
                var claimable = slot.GetClaimableOutputs();
                if (!Positive(claimable)) continue;
                if (claimable > capacity - stock + Epsilon)
                {
                    _status += claimable > capacity + Epsilon
                        ? $"; slot {i + 1} completed output exceeds bag capacity: manual collection required"
                        : $"; slot {i + 1} waiting for {Name(output)} bag space";
                    continue;
                }
                return Claim(manager, i, output, claimable, stock);
            }
            if (!allowNewWork) return false;
            var schedule = RefineryForecast.Plan(_snapshot, ScheduleDemands(DemandScope.Recipes), usable,
                RefineryManager.SECONDS_PER_CYCLE, Math.Clamp(Plugin.Config.RefineryMaximumBatchOutputs.Value, 1, 100));
            foreach (var job in schedule.Jobs)
            {
                if (job.StartSeconds > Epsilon) continue;
                var need = _needs.FirstOrDefault(n => Key(n.Output) == job.OutputKey);
                if (need != null && Empty(manager.slots[job.SlotIndex]))
                    return Start(manager, job.SlotIndex, need, job.Outputs, ProtectedRawStock(need.Raw));
            }
            var surplusRecipes = RefineryForecast.Plan(_snapshot, ScheduleDemands(DemandScope.SurplusRecipes), usable,
                RefineryManager.SECONDS_PER_CYCLE, IdleBatchLimit());
            foreach (var job in surplusRecipes.Jobs)
            {
                if (job.StartSeconds > Epsilon) continue;
                var need = _needs.FirstOrDefault(n => Key(n.Output) == job.OutputKey);
                if (need != null && Empty(manager.slots[job.SlotIndex]))
                    return Start(manager, job.SlotIndex, need, job.Outputs, ProtectedRawStock(need.Raw), idle: true);
            }
            // Only funded recipe work can occupy a slot ahead of background stock. Idle work
            // uses short batches and ranks projected fullness, so pending output also balances slots.
            if (Plugin.Config.RefineryIdleStockEnabled.Value)
            {
                var idleSchedule = RefineryForecast.Plan(_snapshot, IdleDemands(), usable,
                    RefineryManager.SECONDS_PER_CYCLE, IdleBatchLimit());
                foreach (var job in idleSchedule.Jobs)
                {
                    if (job.StartSeconds > Epsilon) continue;
                    var need = _needs.FirstOrDefault(n => Key(n.Output) == job.OutputKey);
                    if (need != null && Empty(manager.slots[job.SlotIndex]))
                        return Start(manager, job.SlotIndex, need, job.Outputs, ProtectedRawStock(need.Raw), idle: true);
                }
            }
            return _canForecast && TryPremium(manager, usable, requests);
        }
        catch (Exception ex)
        {
            _needs.Clear(); // A failed read must not leave a live travel target.
            _lastSnapshotAt = -100;
            _nextPollAt = now + 10;
            _status = $"state unavailable: {ex.GetType().Name}";
            Warn($"Refinery state is unavailable; other strategies continue: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void ReadNeeds(RefineryManager manager, IReadOnlyList<RefineryRecipeRequest> requests)
    {
        _needs.Clear();
        _directRecipeStock.Clear();
        var sources = manager.GetRefinableDrops();
        if (sources == null) return;
        var rawByOutput = new Dictionary<string, Drop>(StringComparer.Ordinal);
        foreach (var raw in sources)
            if (raw != null && !raw.locked && raw.refinedTarget != null && !Same(raw, raw.refinedTarget) &&
                RawStock(raw, out _, out _) && Stock(raw.refinedTarget, out _, out _))
                rawByOutput.TryAdd(Key(raw.refinedTarget), raw);
        // Several simultaneous quests can use the same active effect. Reserve the largest requested
        // number of copies of that item, not one identical recipe per quest.
        foreach (var group in requests.Where(r => r.Item != null && r.IsApplicable).GroupBy(r => r.Item.GetInstanceID()))
        {
            var first = group.OrderBy(r => r.Priority).First();
            var request = first with { Copies = group.Max(r => r.Copies), AllowRawFarming = group.Any(r => r.AllowRawFarming) };
            if (request.Copies <= 0 || !CraftableCatalog.HasUnlockedRecipe(request.Item)) continue;
            var requirements = request.Item.GetRequirements();
            if (requirements == null) continue;
            var possible = true;
            foreach (var r in requirements)
                if (r?.material != null && !r.divinityPoints && r.amount > 0 &&
                    (!Stock(r.material, out _, out var cap) || r.amount + MaterialReservePolicy.MinimumReserve(r.material) > cap))
                    possible = false;
            if (!possible) continue;
            foreach (var r in requirements)
            {
                if (r?.material == null || r.divinityPoints || !Positive(r.amount)) continue;
                var key = Key(r.material);
                var wanted = r.amount * Math.Clamp(request.Copies, 1, 10);
                if (!Positive(wanted)) continue;
                if (!rawByOutput.TryGetValue(key, out var raw))
                {
                    _directRecipeStock.TryGetValue(key, out var direct);
                    _directRecipeStock[key] = direct + wanted;
                    continue;
                }
                var need = _needs.FirstOrDefault(n => Same(n.Output, r.material));
                if (need == null) _needs.Add(need = new Need(raw, r.material));
                need.RecipeWanted += wanted;
                if (request.AllowRawFarming) need.FarmWanted += wanted;
                if (request.Priority < need.Priority)
                { need.Purpose = request.Purpose ?? request.Item.name; need.QuestPlanId = request.QuestPlanId; }
                need.Priority = Math.Min(need.Priority, request.Priority);
            }
        }
        foreach (var need in _needs)
        {
            need.RecipeWanted = Math.Min(need.Output.GetMaxAmount(),
                need.RecipeWanted + MaterialReservePolicy.MinimumReserve(need.Output));
            need.Wanted = need.RecipeWanted;
            if (need.FarmWanted > 0) need.FarmWanted = Math.Min(need.Output.GetMaxAmount(),
                need.FarmWanted + MaterialReservePolicy.MinimumReserve(need.Output));
        }
        if (!Plugin.Config.RefineryIdleStockEnabled.Value) return;
        var fillCapacity = Plugin.Config.RefineryFillIdleSlotsToCapacity.Value;
        var idleTarget = Math.Clamp(Plugin.Config.RefineryIdleStockPerMaterial.Value, 0, 100);
        if (!fillCapacity && idleTarget == 0) return;
        foreach (var raw in rawByOutput.Values)
        {
            var need = _needs.FirstOrDefault(n => Same(n.Output, raw.refinedTarget));
            if (need == null) _needs.Add(need = new Need(raw, raw.refinedTarget));
            need.Wanted = fillCapacity ? need.Output.GetMaxAmount()
                : Math.Min(need.Output.GetMaxAmount(), Math.Max(need.Wanted, idleTarget));
        }
    }

    private void ReadQueues(RefineryManager manager)
    {
        _pending.Clear();
        _snapshot.Clear();
        _canForecast = true;
        for (var i = 0; i < manager.slots.Length; i++)
        {
            var slot = manager.slots[i];
            if (Empty(slot)) { _owned.Remove(i); continue; }
            var output = slot?.material?.refinedTarget;
            if (output == null || !NonNegative(slot.TotalOutputs) || !NonNegative(slot.amountClaimed) ||
                !NonNegative(slot.remainingTime) || slot.amountClaimed > slot.TotalOutputs + Epsilon)
                throw new InvalidOperationException($"Invalid native refinery slot {i + 1}");
            var unclaimed = slot.TotalOutputs - slot.amountClaimed;
            var claimable = slot.GetClaimableOutputs();
            if (!NonNegative(claimable) || claimable > unclaimed + Epsilon || unclaimed > int.MaxValue ||
                Math.Abs(unclaimed - Math.Round(unclaimed)) > Epsilon || Math.Abs(claimable - Math.Round(claimable)) > Epsilon)
                throw new InvalidOperationException($"Invalid native output count in slot {i + 1}");
            _pending.TryGetValue(Key(output), out var pending);
            _pending[Key(output)] = pending + unclaimed;
            var owned = IsOwned(i, slot);
            if (!owned) _owned.Remove(i);
            var canReuse = Stock(output, out var outputStock, out var outputCapacity) &&
                unclaimed <= outputCapacity - outputStock + Epsilon;
            _snapshot.Add(new RefineryForecastSlot(i, Key(output), slot.remainingTime,
                (int)Math.Round(unclaimed - claimable), (int)Math.Round(claimable), owned, canReuse));
        }
        foreach (var slot in manager.slots)
        {
            var output = slot?.material?.refinedTarget;
            if (output == null) continue;
            // A bag-blocked queue is not a reusable future slot. Do not pay using an optimistic forecast.
            if (!Stock(output, out var stock, out var capacity) || Pending(output) > capacity - stock + Epsilon)
                _canForecast = false;
        }
    }

    private bool Claim(RefineryManager manager, int index, Drop output, double claimable, double before)
    {
        var detail = $"slot={index + 1}, output={Name(output)}, requested={claimable:R}, stockBefore={before:R}";
        try
        {
            var originalSlot = manager.slots[index];
            var originalPointer = originalSlot.Pointer;
            var originalRaw = originalSlot.material;
            var originalTotal = originalSlot.totalAmount;
            var originalClaimed = originalSlot.amountClaimed;
            var originalOutputs = originalSlot.TotalOutputs;
            var originalTime = originalSlot.remainingTime;
            Exception invocationFailure = null;
            try
            {
                // Current generated ValueTuple<double,double>.Item1/Item2 use the broken
                // PointerToValueGeneric(fieldAddress, true, false) boxing path. Native ClaimSlot
                // itself is synchronous. Ignore that return wrapper and verify its actual writes.
                _ = manager.ClaimSlot(index);
            }
            catch (Exception ex) { invocationFailure = ex; }
            var after = output.amount;
            var added = after - before;
            var slot = manager.slots[index];
            var expectedClaimed = originalClaimed + added;
            var shouldClear = expectedClaimed + Epsilon >= originalOutputs;
            var accounted = slot != null && slot.Pointer == originalPointer &&
                (shouldClear ? Empty(slot) : Same(slot.material, originalRaw) &&
                 Math.Abs(slot.totalAmount - originalTotal) <= Epsilon &&
                 NonNegative(slot.remainingTime) && slot.remainingTime <= originalTime + Epsilon &&
                 NonNegative(slot.amountClaimed) && Math.Abs(slot.amountClaimed - expectedClaimed) <= Epsilon);
            detail += $", stockAfter={after:R}, added={added:R}, claimedBefore={originalClaimed:R}, " +
                $"claimedAfter={slot?.amountClaimed.ToString("R") ?? "missing"}, totalOutputs={originalOutputs:R}, " +
                $"cleared={slot != null && Empty(slot)}, slotAccounted={accounted}, " +
                $"nativeException={invocationFailure?.GetType().Name ?? "none"}";
            if (invocationFailure != null || !NonNegative(after) || !Positive(added) || added > claimable + Epsilon || !accounted)
                throw new InvalidOperationException("Native claim or inventory/queue confirmation was uncertain", invocationFailure);
            Log($"Refinery collected {added:0.##}/{claimable:0.##} {Name(output)} from slot {index + 1}; inventory and queue verified.");
            ReadQueues(manager);
            _lastSnapshotAt = Time.unscaledTime;
            return true;
        }
        catch (Exception ex) { Suspend("claim", new InvalidOperationException($"{ex.Message}; {detail}", ex)); return false; }
    }

    private bool Start(RefineryManager manager, int index, Need need, int count, double reserve, bool idle = false)
    {
        var raw = need.Raw;
        var cost = count * RefineryManager.MATERIALS_PER_OUTPUT;
        try
        {
            if (count <= 0 || raw.locked || index >= manager.unlockedSlots || !Empty(manager.slots[index]) ||
                !RawStock(raw, out var before, out _) || before - cost + Epsilon < reserve ||
                !Stock(need.Output, out var outputStock, out var capacity) ||
                outputStock + Pending(need.Output) + count > capacity + Epsilon) return false;
            if (!manager.StartRefining(index, raw, count))
            {
                if (Math.Abs(raw.amount - before) > Epsilon || !Empty(manager.slots[index]))
                    throw new InvalidOperationException("Start rejected after changing native state");
                return false;
            }
            var slot = manager.slots[index];
            if (Math.Abs(before - raw.amount - cost) > Epsilon || !Same(slot.material, raw) ||
                Math.Abs(slot.TotalOutputs - count) > Epsilon || slot.amountClaimed > Epsilon ||
                Math.Abs(slot.remainingTime - count * RefineryManager.SECONDS_PER_CYCLE) > 1)
                throw new InvalidOperationException("Start result did not match the charge/queue");
            _owned[index] = new OwnedQueue(Key(raw), slot.totalAmount, slot.Pointer,
                slot.remainingTime, slot.amountClaimed, Time.time);
            ReadQueues(manager);
            _lastSnapshotAt = Time.unscaledTime;
            Log($"Refinery queued {count} {Name(need.Output)} using {cost:0} {Name(raw)}; " +
                $"slot={index + 1}, duration={count * RefineryManager.SECONDS_PER_CYCLE / 60:0.#}m, " +
                $"purpose={(idle ? "background stock refill" : need.Purpose ?? "recipe supply")}, raw reserve={reserve:0}.");
            return true;
        }
        catch (Exception ex) { Suspend("start", ex); return false; }
    }

    private bool TryPremium(RefineryManager manager, int usable, IReadOnlyList<RefineryRecipeRequest> requests)
    {
        var config = Plugin.Config;
        if (!config.RefinerySpeedupEnabled.Value && !config.RefineryBuySlotsEnabled.Value) return false;
        if (Time.unscaledTime < _nextPremiumAt) return false;
        _nextPremiumAt = Time.unscaledTime + 10;
        var bestSlot = -1;
        var bestCost = 0d;
        var bestGain = 0d;
        if (config.RefinerySpeedupEnabled.Value)
            foreach (var request in requests.Where(r => r.IsApplicable && r.Immediate && r.AllowPremium).OrderBy(r => r.Priority))
            {
                if (!FundedDemands(request, out var demands) || demands.Count == 0) continue;
                // Deadline decisions use only fixed native queues: no optimistic future batch
                // scheduling or raw-farming estimate may authorize an event-window payment.
                if (demands.Any(d => !_pending.TryGetValue(d.OutputKey, out var committed)
                                    ? d.MissingOutputs > 0 : d.MissingOutputs > committed + Epsilon)) continue;
                foreach (var slot in _snapshot)
                {
                    if (!slot.IsOwned || slot.RemainingOutputs <= 0 ||
                        !demands.Any(d => d.OutputKey == slot.OutputKey && d.MissingOutputs > 0)) continue;
                    var cost = manager.GetSpeedUpCost(slot.Index);
                    var estimate = RefineryForecast.EvaluateSpeedup(_snapshot, demands, usable,
                        RefineryManager.SECONDS_PER_CYCLE, slot.Index, cost,
                        config.RefineryMinimumSecondsSavedPerJewel.Value,
                        config.RefineryMinimumSpeedupMinutes.Value * 60, request.DeadlineSeconds,
                        Math.Clamp(config.RefineryMaximumBatchOutputs.Value, 1, 100));
                    // Missing the whole event is not useful, even if an individual slot is faster.
                    if (!estimate.Eligible || request.DeadlineSeconds.HasValue && !estimate.CanMeetDeadline ||
                        !Positive(cost) || estimate.SavedSeconds / cost <= bestGain) continue;
                    bestSlot = slot.Index;
                    bestCost = cost;
                    bestGain = estimate.SavedSeconds / cost;
                }
            }
        var unlock = false;
        if (config.RefineryBuySlotsEnabled.Value && usable < Math.Min(RefineryManager.MAX_SLOTS,
                Math.Clamp(config.RefineryMaximumSlots.Value, 1, 3)) && usable < manager.slots.Length && Empty(manager.slots[usable]))
        {
            var backlog = ScheduleDemands(DemandScope.PurchaseEligible);
            // Aggregate, not per-recipe: shared raw inventory cannot fund two jobs at once.
            var funded = _needs.Where(n => n.FarmWanted > 0).All(n =>
                Math.Max(0, Missing(n.Output, n.FarmWanted) - Pending(n.Output)) * RefineryManager.MATERIALS_PER_OUTPUT
                <= Math.Max(0, n.Raw.amount - ProtectedRawStock(n.Raw)) + Epsilon);
            if (backlog.Length > 0 && funded)
            {
                var estimate = RefineryForecast.EvaluateNewSlot(_snapshot, backlog, usable,
                    RefineryManager.SECONDS_PER_CYCLE, RefineryManager.UNLOCK_SLOT_COST, 0,
                    config.RefineryMinimumSlotGainMinutes.Value * 60, maxBatchOutputs:
                    Math.Clamp(config.RefineryMaximumBatchOutputs.Value, 1, 100));
                if (estimate.Eligible && estimate.SecondsPerJewel > bestGain)
                { unlock = true; bestCost = RefineryManager.UNLOCK_SLOT_COST; }
            }
        }
        if (!unlock && bestSlot < 0) return false;
        var succeeded = _premium.TryExecute(manager, unlock ? -1 : bestSlot, unlock, bestCost, out var message);
        if (succeeded) { ReadQueues(manager); _lastSnapshotAt = Time.unscaledTime; Log(message); }
        else { _status += "; " + message; Warn(message); }
        return succeeded;
    }

    private enum DemandScope { Recipes, SurplusRecipes, PurchaseEligible }

    private RefineryForecastDemand[] ScheduleDemands(DemandScope scope)
    {
        return _needs.Where(n => scope switch
        {
            DemandScope.Recipes => n.RecipeWanted > 0 && n.Priority < RefineryRecipeRequest.SurplusPriority,
            DemandScope.SurplusRecipes => n.RecipeWanted > 0 && n.Priority >= RefineryRecipeRequest.SurplusPriority,
            _ => n.FarmWanted > 0
        }).Select(n =>
        {
            var wanted = scope == DemandScope.PurchaseEligible ? n.FarmWanted : n.RecipeWanted;
            var affordable = Math.Floor(Math.Max(0, n.Raw.amount - ProtectedRawStock(n.Raw)) /
                RefineryManager.MATERIALS_PER_OUTPUT);
            var capacity = Math.Floor(Math.Max(0, n.Output.GetMaxAmount() - n.Output.amount - Pending(n.Output)));
            return new RefineryForecastDemand(Key(n.Output), Missing(n.Output, wanted), n.Priority,
                (int)Math.Clamp(Math.Min(affordable, capacity), 0, int.MaxValue));
        }).Where(d => d.MissingOutputs > 0).ToArray();
    }

    private int IdleBatchLimit() => Math.Clamp(Plugin.Config.RefineryIdleBatchOutputs.Value, 1,
        Math.Clamp(Plugin.Config.RefineryMaximumBatchOutputs.Value, 1, 100));

    private RefineryForecastDemand[] IdleDemands()
    {
        // Rank only live, affordable deficits. Full-bag/raw-pressure is a tie breaker, not a
        // reason to consume task/Pill/Whetstone reserves. Limit the horizon to one short batch
        // per material so a large native capacity cannot overflow the bounded scheduler.
        return _needs.Where(n => n.Wanted > n.Output.amount + Pending(n.Output) + Epsilon)
            .Select(n => new
            {
                Need = n,
                Projected = n.Output.amount + Pending(n.Output),
                Affordable = Math.Floor(Math.Max(0, n.Raw.amount - ProtectedRawStock(n.Raw)) /
                    RefineryManager.MATERIALS_PER_OUTPUT)
            })
            .Where(n => n.Affordable >= 1)
            .OrderBy(n => n.Projected / Math.Max(1, n.Need.Wanted))
            .ThenByDescending(n => n.Need.Raw.amount / Math.Max(1, n.Need.Raw.GetMaxAmount()))
            .ThenBy(n => Key(n.Need.Output), StringComparer.Ordinal)
            .Select((n, rank) =>
            {
                var count = (int)Math.Min(IdleBatchLimit(), Math.Min(n.Affordable,
                    Math.Floor(n.Need.Wanted - n.Projected + Epsilon)));
                return new RefineryForecastDemand(Key(n.Need.Output),
                    Missing(n.Need.Output, n.Projected + Math.Max(0, count)), RefineryRecipeRequest.IdlePriority + rank, Math.Max(0, count));
            }).Where(n => n.MaximumNewOutputs > 0).ToArray();
    }

    private bool FundedDemands(RefineryRecipeRequest request, out List<RefineryForecastDemand> demands)
    {
        demands = new();
        if (!request.IsApplicable || !CraftableCatalog.HasUnlockedRecipe(request.Item)) return false;
        var requirements = request.Item.GetRequirements();
        if (requirements == null) return false;
        foreach (var r in requirements)
        {
            if (r == null || r.amount <= 0) continue;
            if (r.divinityPoints || r.material == null) return false;
            var need = _needs.FirstOrDefault(n => Same(n.Output, r.material));
            // Common crafting may require retained copies; a one-copy forecast would claim success
            // even though TryCraft still refuses to spend the last reserved recipe.
            var wanted = r.amount * request.Copies + MaterialReservePolicy.MinimumReserve(r.material);
            if (need == null)
            {
                if (r.material.amount + Epsilon < wanted) return false;
                continue;
            }
            if (wanted > need.Output.GetMaxAmount() + Epsilon) return false;
            var missing = Missing(need.Output, wanted);
            var rawMissing = Math.Max(0, missing - Pending(need.Output)) * RefineryManager.MATERIALS_PER_OUTPUT;
            if (rawMissing > Math.Max(0, need.Raw.amount - ProtectedRawStock(need.Raw)) + Epsilon) return false;
            demands.Add(new RefineryForecastDemand(Key(need.Output), missing));
        }
        return true;
    }

    internal MaterialNeed FindRawMaterialNeed(QuestPlan plan, bool suppressTravel)
    {
        if (!Plugin.Config.SupplementalRefiningEnabled.Value || !Plugin.Config.RefineryRawMaterialFarming.Value ||
            _writesSuspended || Time.unscaledTime - _lastSnapshotAt > 10 || suppressTravel) return null;
        var currentMap = MapController.instance?.selectedMap;
        var onRouteOnly = MaterialPlanner.BlocksIdleSurplusFarming(plan);
        if (onRouteOnly && plan?.TargetMap != null && !MapEfficiencyEstimator.SameMap(currentMap, plan.TargetMap)) return null;
        MaterialNeed best = null;
        var bestSeconds = double.PositiveInfinity;
        var bestPriority = int.MaxValue;
        foreach (var need in _needs.Where(n => n.FarmWanted > 0).OrderBy(n => n.Priority))
        {
            if (need.Priority > bestPriority) break;
            var gap = Math.Max(0, Missing(need.Output, need.FarmWanted) - Pending(need.Output));
            if (gap <= 0 || !Stock(need.Raw, out var stock, out var capacity)) continue;
            var reserve = ProtectedRawStock(need.Raw);
            if (reserve + RefineryManager.MATERIALS_PER_OUTPUT > capacity + Epsilon) continue;
            var desired = Math.Min(capacity, reserve + gap * RefineryManager.MATERIALS_PER_OUTPUT);
            if (stock + Epsilon >= desired) continue;
            var efficiency = onRouteOnly ? MapEfficiencyEstimator.EvaluateForMaterial(currentMap, need.Raw, desired - stock)
                : MapEfficiencyEstimator.BestForMaterial(need.Raw, desired - stock);
            var density = MapEfficiencyEstimator.EffectiveDensity(efficiency);
            if (efficiency?.Map == null || density <= 0 || !double.IsFinite(efficiency.ExpectedEligibleKills) ||
                !QuestDirector.IsStandardAvailableMap(efficiency.Map)) continue;
            var seconds = MapEfficiencyEstimator.EstimatedTravelSeconds(efficiency.Map) +
                efficiency.ExpectedEligibleKills / MapEfficiencyEstimator.EstimatedTargetKillsPerMinute(density,
                    efficiency.TargetsGiant, includeTemporaryShards: false) * 60;
            if (seconds >= bestSeconds) continue;
            bestSeconds = seconds;
            bestPriority = need.Priority;
            best = new MaterialNeed(need.Raw, efficiency.Map, desired, stock, 4, density,
                efficiency.ExpectedEligibleKills, efficiency.TargetsGiant,
                $"Refinery supply: {Name(need.Output)} for {need.Purpose}; queued output already deducted",
                onRouteOnly ? plan?.InstanceId ?? 0 : 0, opportunistic: onRouteOnly, forRefinery: true);
        }
        return best;
    }

    internal bool StillNeedsRaw(Drop raw)
    {
        if (!Plugin.Config.SupplementalRefiningEnabled.Value || !Plugin.Config.RefineryRawMaterialFarming.Value ||
            _writesSuspended || Time.unscaledTime - _lastSnapshotAt > 10) return false;
        var need = _needs.FirstOrDefault(n => Same(n.Raw, raw) && n.FarmWanted > 0);
        if (need == null) return false;
        var gap = Math.Max(0, Missing(need.Output, need.FarmWanted) - Pending(need.Output));
        return gap > 0 && raw.amount + Epsilon < Math.Min(raw.GetMaxAmount(),
            ProtectedRawStock(raw) + gap * RefineryManager.MATERIALS_PER_OUTPUT);
    }

    internal double ProtectedRawStock(Drop raw)
    {
        var name = CraftableCatalog.Normalize(raw.name);
        var fallback = name.EndsWith("titanium", StringComparison.Ordinal) ? 9d :
            name.EndsWith("root", StringComparison.Ordinal) ? 50d : name.EndsWith("slime", StringComparison.Ordinal) ? 80d : 0d;
        var critical = RecipeAmount(_safetyCatalog.Get(SmartItem.Whetstone), raw) * 3 + RecipeAmount(CraftableItems.list?.RagePill, raw);
        _directRecipeStock.TryGetValue(Key(raw), out var direct);
        var external = _externalReserve?.Invoke(raw) ?? 0;
        if (!NonNegative(external)) return double.PositiveInfinity;
        return Math.Max(MaterialReservePolicy.MinimumReserve(raw) + Math.Max(fallback, critical) + direct, external);
    }

    internal static double RecipeAmount(TemporaryCraftableItem item, Drop raw)
    {
        if (item == null) return 0;
        var requirements = item.GetRequirements();
        if (requirements == null) return 0;
        var amount = 0d;
        foreach (var r in requirements)
            if (r != null && !r.divinityPoints && Positive(r.amount) && Same(r.material, raw)) amount += r.amount;
        return amount;
    }
    private bool IsOwned(int index, RefinerySlot slot)
    {
        if (!_owned.TryGetValue(index, out var owned) || slot.Pointer != owned.Pointer ||
            Key(slot.material) != owned.RawKey || Math.Abs(slot.totalAmount - owned.TotalAmount) >= Epsilon ||
            slot.amountClaimed + Epsilon < owned.Claimed ||
            slot.remainingTime > Math.Max(0, owned.Remaining - (Time.time - owned.ObservedAt)) + 0.25)
            return false;
        _owned[index] = owned with { Remaining = slot.remainingTime, Claimed = slot.amountClaimed, ObservedAt = Time.time };
        return true;
    }
    private double Pending(Drop output) => _pending.TryGetValue(Key(output), out var value) ? value : 0;
    private static int Missing(Drop output, double wanted) => (int)Math.Clamp(Math.Ceiling(wanted - output.amount - Epsilon), 0, int.MaxValue);
    internal static bool FeatureUnlocked()
    {
        if (Upgrades.list?.BookOfRefinement?.bought != true) return false;
        var buildings = PlayerInventory.instance?.buildings;
        if (buildings == null) return false;
        foreach (var building in buildings)
            if (building != null && building.id == RefineryBuildingId) return building.built;
        return false;
    }
    private static bool Empty(RefinerySlot slot) => slot != null && !slot.IsActive && slot.material == null &&
        NonNegative(slot.totalAmount) && slot.totalAmount <= Epsilon && NonNegative(slot.amountClaimed) &&
        slot.amountClaimed <= Epsilon && NonNegative(slot.remainingTime) && slot.remainingTime <= Epsilon;
    private static bool Stock(Drop drop, out double stock, out double capacity)
    {
        stock = drop.amount;
        capacity = drop.GetMaxAmount();
        return NonNegative(stock) && Positive(capacity) && stock <= capacity + Epsilon;
    }
    private static bool RawStock(Drop drop, out double stock, out double capacity)
    {
        stock = drop.amount;
        capacity = drop.GetMaxAmount();
        // Reducing a legitimate over-cap input stack is safe; only output space must be bounded.
        return NonNegative(stock) && Positive(capacity);
    }
    internal static bool Same(Drop left, Drop right) => left != null && right != null &&
        (left == right || !string.IsNullOrEmpty(left.id) && left.id == right.id);
    private static string Key(Drop drop) => !string.IsNullOrEmpty(drop.id) ? drop.id : drop.GetInstanceID().ToString();
    private static string Name(Drop drop) => drop?.localizedName ?? drop?.name ?? "unknown";
    private static bool Positive(double value) => double.IsFinite(value) && value > Epsilon;
    private static bool NonNegative(double value) => double.IsFinite(value) && value >= 0;
    private void Suspend(string operation, Exception ex)
    {
        _writesSuspended = true;
        _needs.Clear();
        _status = "native writes suspended: uncertain " + operation;
        SmartLog.Warning(SmartLogCategory.Material, $"Refinery {operation} was uncertain; no retry/cancel/discard. " +
            $"Check the refinery before restarting: {ex.GetType().Name}: {ex.Message}");
    }
    private void Log(string message)
    {
        if (Time.unscaledTime < _nextLogAt) return;
        _nextLogAt = Time.unscaledTime + 15;
        SmartLog.Info(SmartLogCategory.Material, message);
    }
    private void Warn(string message)
    {
        if (Time.unscaledTime < _nextFailureLogAt) return;
        _nextFailureLogAt = Time.unscaledTime + 60;
        SmartLog.Warning(SmartLogCategory.Material, message);
    }
    private readonly record struct OwnedQueue(string RawKey, double TotalAmount, IntPtr Pointer,
        double Remaining, double Claimed, float ObservedAt);
    private sealed class Need
    {
        internal readonly Drop Raw, Output;
        internal double RecipeWanted, Wanted, FarmWanted;
        internal int QuestPlanId;
        internal int Priority = RefineryRecipeRequest.IdlePriority;
        internal string Purpose;
        internal Need(Drop raw, Drop output) { Raw = raw; Output = output; }
    }
}

using Il2Cpp;
using UnityEngine;

namespace SmartStrategy;

internal readonly record struct RefineryRecipeRequest(TemporaryCraftableItem Item, int Copies, int Priority,
    bool Immediate = false, double? DeadlineSeconds = null);

// Uses the native backend, never the Village popup. No timer editing, cancellation or discarding.
internal sealed class RefinedCraftingSupply
{
    private const double Epsilon = 0.0001;
    private const string RefineryBuildingId = "ihda7gqmgppigg8u88lq";
    private readonly CraftableCatalog _safetyCatalog = new();
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
            var empty = Enumerable.Range(0, usable).Where(i => Empty(manager.slots[i])).ToArray();
            if (empty.Length > 0)
                foreach (var need in _needs.OrderBy(n => n.Priority)
                             .ThenBy(n => (n.Output.amount + Pending(n.Output)) / Math.Max(1, n.Wanted)))
                {
                    if (!Stock(need.Output, out var stock, out var capacity)) continue;
                    var gap = Math.Min(need.Wanted, capacity) - stock - Pending(need.Output);
                    var reserve = ProtectedRawStock(need.Raw);
                    var affordable = Math.Floor((need.Raw.amount - reserve + Epsilon) / RefineryManager.MATERIALS_PER_OUTPUT);
                    var batch = Math.Min(Math.Clamp(Plugin.Config.RefineryMaximumBatchOutputs.Value, 1, 100),
                        Math.Min(Math.Floor(capacity - stock - Pending(need.Output) + Epsilon),
                            Math.Min(Math.Ceiling(gap - Epsilon), affordable)));
                    if (!Positive(batch)) continue;
                    return Start(manager, empty[0], need, (int)batch, reserve);
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
                Stock(raw, out _, out _) && Stock(raw.refinedTarget, out _, out _))
                rawByOutput.TryAdd(Key(raw.refinedTarget), raw);
        foreach (var request in requests)
        {
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
                need.Priority = Math.Min(need.Priority, request.Priority);
                need.Purpose = request.Item.name;
            }
        }
        foreach (var need in _needs)
        {
            need.RecipeWanted = Math.Min(need.Output.GetMaxAmount(),
                need.RecipeWanted + MaterialReservePolicy.MinimumReserve(need.Output));
            need.Wanted = need.RecipeWanted;
        }
        if (!Plugin.Config.RefineryIdleStockEnabled.Value) return;
        var idleTarget = Math.Clamp(Plugin.Config.RefineryIdleStockPerMaterial.Value, 0, 100);
        if (idleTarget == 0) return;
        foreach (var raw in rawByOutput.Values)
        {
            var need = _needs.FirstOrDefault(n => Same(n.Output, raw.refinedTarget));
            if (need == null) _needs.Add(need = new Need(raw, raw.refinedTarget));
            need.Wanted = Math.Min(need.Output.GetMaxAmount(), Math.Max(need.Wanted, idleTarget));
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
            if (!NonNegative(claimable) || claimable > unclaimed + Epsilon || unclaimed > int.MaxValue)
                throw new InvalidOperationException($"Invalid native output count in slot {i + 1}");
            _pending.TryGetValue(Key(output), out var pending);
            _pending[Key(output)] = pending + unclaimed;
            var owned = IsOwned(i, slot);
            if (!owned) _owned.Remove(i);
            _snapshot.Add(new RefineryForecastSlot(i, Key(output), slot.remainingTime,
                (int)Math.Round(unclaimed - claimable), (int)Math.Round(claimable), owned));
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
        try
        {
            var result = manager.ClaimSlot(index);
            if (Math.Abs(output.amount - before - claimable) > Epsilon ||
                Math.Abs(result.Item1 - claimable) > Epsilon || Math.Abs(result.Item2) > Epsilon)
                throw new InvalidOperationException("Claim result did not match the inventory change");
            Log($"Refinery collected {claimable:0} {Name(output)} from slot {index + 1}.");
            ReadQueues(manager);
            _lastSnapshotAt = Time.unscaledTime;
            return true;
        }
        catch (Exception ex) { Suspend("claim", ex); return false; }
    }

    private bool Start(RefineryManager manager, int index, Need need, int count, double reserve)
    {
        var raw = need.Raw;
        var cost = count * RefineryManager.MATERIALS_PER_OUTPUT;
        try
        {
            if (count <= 0 || raw.locked || index >= manager.unlockedSlots || !Empty(manager.slots[index]) ||
                !Stock(raw, out var before, out _) || before - cost + Epsilon < reserve ||
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
                $"purpose={need.Purpose ?? "surplus buffer"}, raw reserve={reserve:0}.");
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
            foreach (var request in requests.Where(r => r.Immediate).OrderBy(r => r.Priority))
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
                        config.RefineryMinimumSpeedupMinutes.Value * 60, request.DeadlineSeconds);
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
            var backlog = _needs.Where(n => n.RecipeWanted > 0)
                .Select(n => new RefineryForecastDemand(Key(n.Output), Missing(n.Output, n.RecipeWanted)))
                .Where(d => d.MissingOutputs > 0).ToArray();
            // Aggregate, not per-recipe: shared raw inventory cannot fund two jobs at once.
            var funded = _needs.Where(n => n.RecipeWanted > 0).All(n =>
                Math.Max(0, Missing(n.Output, n.RecipeWanted) - Pending(n.Output)) * RefineryManager.MATERIALS_PER_OUTPUT
                <= Math.Max(0, n.Raw.amount - ProtectedRawStock(n.Raw)) + Epsilon);
            if (backlog.Length > 0 && funded)
            {
                var estimate = RefineryForecast.EvaluateNewSlot(_snapshot, backlog, usable,
                    RefineryManager.SECONDS_PER_CYCLE, RefineryManager.UNLOCK_SLOT_COST, 0,
                    config.RefineryMinimumSlotGainMinutes.Value * 60);
                if (estimate.Eligible && estimate.SecondsPerJewel > bestGain)
                { unlock = true; bestCost = RefineryManager.UNLOCK_SLOT_COST; }
            }
        }
        if (!unlock && bestSlot < 0) return false;
        var limits = new RefineryPremiumLimits(Math.Max(config.RefineryJewelReserve.Value,
            config.PaidSoulsJewelReserve.Value), config.RefineryDailyJewelBudget.Value);
        var succeeded = _premium.TryExecute(manager, unlock ? -1 : bestSlot, unlock, bestCost, limits, out var message);
        if (succeeded) { ReadQueues(manager); _lastSnapshotAt = Time.unscaledTime; Log(message); }
        else { _status += "; " + message; Warn(message); }
        return succeeded;
    }

    private bool FundedDemands(RefineryRecipeRequest request, out List<RefineryForecastDemand> demands)
    {
        demands = new();
        if (!CraftableCatalog.HasUnlockedRecipe(request.Item)) return false;
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
        foreach (var need in _needs.Where(n => n.RecipeWanted > 0).OrderBy(n => n.Priority))
        {
            if (need.Priority > bestPriority) break;
            var gap = Math.Max(0, Missing(need.Output, need.RecipeWanted) - Pending(need.Output));
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
        var need = _needs.FirstOrDefault(n => Same(n.Raw, raw) && n.RecipeWanted > 0);
        if (need == null) return false;
        var gap = Math.Max(0, Missing(need.Output, need.RecipeWanted) - Pending(need.Output));
        return gap > 0 && raw.amount + Epsilon < Math.Min(raw.GetMaxAmount(),
            ProtectedRawStock(raw) + gap * RefineryManager.MATERIALS_PER_OUTPUT);
    }

    private double ProtectedRawStock(Drop raw)
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
    private static bool FeatureUnlocked()
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
        internal double RecipeWanted, Wanted;
        internal int Priority = 100;
        internal string Purpose;
        internal Need(Drop raw, Drop output) { Raw = raw; Output = output; }
    }
}

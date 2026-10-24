using Il2Cpp;

namespace SmartStrategy;

internal sealed class MaterialNeed
{
    internal Drop Material { get; }
    internal BaseMap Map { get; }
    internal double Desired { get; }
    internal double Current { get; }
    internal int Priority { get; }
    internal double TargetEnemiesPerPattern { get; }
    internal double ExpectedEligibleKills { get; }
    internal bool GiantDrop { get; }
    internal string Purpose { get; }
    internal int QuestPlanId { get; }
    internal bool Opportunistic { get; }
    internal bool ForRefinery { get; }
    internal IReadOnlyList<SmartItem> QuestItems { get; }
    internal double Severity => Desired <= 0 ? 0 : Math.Clamp((Desired - Current) / Desired, 0, 1);

    internal MaterialNeed(Drop material, BaseMap map, double desired, double current, int priority,
        double targetEnemiesPerPattern, double expectedEligibleKills, bool giantDrop, string purpose = "Stock supply",
        int questPlanId = 0, bool opportunistic = false, bool forRefinery = false,
        IEnumerable<SmartItem> questItems = null)
    {
        Material = material;
        Map = map;
        Desired = desired;
        Current = current;
        Priority = priority;
        TargetEnemiesPerPattern = targetEnemiesPerPattern;
        ExpectedEligibleKills = expectedEligibleKills;
        GiantDrop = giantDrop;
        Purpose = purpose;
        QuestPlanId = questPlanId;
        Opportunistic = opportunistic;
        ForRefinery = forRefinery;
        QuestItems = questItems?.Distinct().ToArray() ?? Array.Empty<SmartItem>();
    }
}

internal sealed class MaterialPlanner
{
    private readonly CraftableCatalog _catalog;
    private readonly RefinedCraftingSupply _refinery;
    private readonly Dictionary<int, RefineryRecipeRequest> _questRefineryRequests = new();
    private float _nextRefinedQuestRefresh;
    private readonly Dictionary<string, float> _questSupplySkipLogUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<int, float> _nextCombinedStaffLogAtByQuest = new();
    private readonly Dictionary<int, IReadOnlyList<MapEfficiency>> _materialEfficiencyCache = new();
    private readonly Dictionary<int, Dictionary<SmartItem, int>> _recommendedKillStaffUsesByQuest = new();

    internal MaterialPlanner(CraftableCatalog catalog, RefinedCraftingSupply refinery)
    { _catalog = catalog; _refinery = refinery; }

    internal IReadOnlyList<RefineryRecipeRequest> RefinedQuestRequests(IEnumerable<QuestPlan> activePlans)
    {
        var plans = activePlans.Where(p => p?.Quest != null && p.Current < p.Goal && QuestApi.CanProgressNow(p.Quest))
            .GroupBy(p => p.InstanceId).Select(g => g.First()).ToArray();
        var ids = plans.Select(p => p.InstanceId).ToHashSet();
        foreach (var stale in _questRefineryRequests.Keys.Where(id => !ids.Contains(id)).ToArray()) _questRefineryRequests.Remove(stale);
        if (UnityEngine.Time.unscaledTime >= _nextRefinedQuestRefresh)
        {
            _nextRefinedQuestRefresh = UnityEngine.Time.unscaledTime + 10;
            // Critical-hit quests are background tasks, but their item supply still needs the same
            // recipe/value model as the primary quest. The route arbiter retains primary-map priority.
            foreach (var plan in plans.Where(p => p.Type == QuestKind.DoCriticals)) FindQuestSupplyNeed(plan);
        }
        return _questRefineryRequests.Values.Where(r => r.IsApplicable).ToArray();
    }

    internal double PlannedRawReserve(Drop raw, IEnumerable<QuestPlan> activePlans)
    {
        // Concurrent quest recommendations can share staff coverage. Retain the largest planned
        // count of each staff rather than multiplying it by the number of simultaneous quests.
        var uses = new Dictionary<SmartItem, int>();
        foreach (var plan in activePlans.Where(p => p?.Quest != null).GroupBy(p => p.InstanceId).Select(g => g.First()))
        {
            if (!_recommendedKillStaffUsesByQuest.TryGetValue(plan.InstanceId, out var recommendation)) continue;
            foreach (var pair in recommendation)
                if (QuestEffectPolicy.CanSpend(plan.Quest, pair.Key))
                    uses[pair.Key] = Math.Max(uses.TryGetValue(pair.Key, out var current) ? current : 0, pair.Value);
        }
        var total = 0d;
        foreach (var pair in uses)
            total += RefinedCraftingSupply.RecipeAmount(_catalog.Get(pair.Key), raw) * Math.Max(0, pair.Value);
        return total;
    }

    internal MaterialNeed FindDedicatedCraftSupplyNeed(SmartItem kind, int requestedUses, string purpose,
        out int targetUses, out bool ready, out string unavailableReason)
    {
        targetUses = 0;
        ready = false;
        unavailableReason = null;
        var mapController = MapController.instance;
        if (mapController == null || !mapController.initialized || mapController.maps == null)
        {
            unavailableReason = "Map data is not initialized";
            return null;
        }

        var item = _catalog.Get(kind);
        if (item == null)
        {
            unavailableReason = $"{kind} is unresolved or locked";
            return null;
        }

        try
        {
            if (!item.TabVisible())
            {
                unavailableReason = $"{DisplayCraftableName(item, kind)} is locked or its crafting tab is hidden";
                return null;
            }

            var requirements = item.GetRequirements();
            if (requirements == null)
            {
                unavailableReason = $"Unable to read the recipe for {DisplayCraftableName(item, kind)}";
                return null;
            }

            targetUses = Math.Max(1, requestedUses);
            var hasMaterialRequirement = false;
            for (var i = 0; i < requirements.Length; i++)
            {
                var requirement = requirements[i];
                var material = requirement?.material;
                if (material == null || requirement.divinityPoints || requirement.amount <= 0) continue;
                hasMaterialRequirement = true;
                double capacity;
                try { capacity = material.GetMaxAmount(); }
                catch { capacity = material.maxAmount; }
                if (!double.IsFinite(capacity) || capacity <= 0) continue;
                var reserve = MaterialReservePolicy.MinimumReserve(material);
                var capacityUses = (int)Math.Max(0,
                    Math.Min(int.MaxValue, Math.Floor(Math.Max(0, capacity - reserve) / requirement.amount)));
                targetUses = Math.Min(targetUses, capacityUses);
            }

            if (!hasMaterialRequirement || targetUses <= 0)
            {
                unavailableReason = hasMaterialRequirement
                    ? $"Material capacity cannot retain at least one {DisplayCraftableName(item, kind)} recipe"
                    : $"{DisplayCraftableName(item, kind)} has no plannable ordinary-material recipe";
                return null;
            }

            _materialEfficiencyCache.Clear();
            MaterialNeed best = null;
            for (var i = 0; i < requirements.Length; i++)
            {
                var requirement = requirements[i];
                var material = requirement?.material;
                if (material == null || requirement.divinityPoints || requirement.amount <= 0) continue;
                double capacity;
                try { capacity = material.GetMaxAmount(); }
                catch { capacity = material.maxAmount; }
                var desired = MaterialReservePolicy.MinimumReserve(material) + requirement.amount * targetUses;
                if (double.IsFinite(capacity) && capacity > 0) desired = Math.Min(desired, capacity);
                if (material.amount + 0.0001 >= desired) continue;
                if (material.locked)
                {
                    unavailableReason = $"Recipe material {DisplayName(material)} is locked";
                    return null;
                }

                var missing = Math.Max(0, desired - material.amount);
                var efficiency = BestForMaterialCached(material, missing);
                var density = MapEfficiencyEstimator.EffectiveDensity(efficiency);
                if (efficiency?.Map == null || density <= 0 || !double.IsFinite(efficiency.ExpectedEligibleKills))
                {
                    unavailableReason = $"Recipe material {DisplayName(material)} has no unlocked automatically farmable map";
                    return null;
                }

                var candidate = new MaterialNeed(material, efficiency.Map, desired, material.amount, 0,
                    density, efficiency.ExpectedEligibleKills, efficiency.TargetsGiant, purpose);
                best = BetterNeed(best, candidate, mapController.selectedMap);
            }

            ready = best == null;
            return best;
        }
        catch (Exception ex)
        {
            targetUses = 0;
            ready = false;
            unavailableReason = $"Unable to read the dedicated supply plan for {kind}: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    internal MaterialNeed FindMostImportantNeed(MaterialNeed activeNeed = null, QuestPlan questPlan = null,
        bool suppressQuestSupplies = false, QuestPlan parallelRagePlan = null,
        bool suppressIdleSurplus = false)
    {
        var mapController = MapController.instance;
        if (mapController == null || !mapController.initialized || mapController.maps == null) return null;
        // One combined-supply enumeration queries the same material across several staff combinations. Cache each
        // map's drop profile for this cycle, but choose the optimal map from each combination's real gap because fixed
        // travel cost makes small local supply and large high-density supply produce different answers.
        _materialEfficiencyCache.Clear();

        // A Portal Weekly briefly releases its quest lock after travel. During protection, do not reuse the previous
        // kill quest's staff supply target or zero-cooldown travel inserts a pointless material round trip.
        if (suppressQuestSupplies && activeNeed != null && activeNeed.QuestPlanId != 0) activeNeed = null;
        if (activeNeed?.Opportunistic == true && (questPlan == null ||
            activeNeed.QuestPlanId != questPlan.InstanceId ||
            !MapEfficiencyEstimator.SameMap(activeNeed.Map, questPlan.TargetMap)))
            activeNeed = null;

        // Once a supply chain starts, do not release the quest plan after one material reaches target. Treat refill
        // thresholds as start thresholds rather than stop thresholds and continue remaining farmable targets, avoiding
        // Material A -> quest map craft -> Material B round trips.
        var activeNeedSatisfied = false;
        if (activeNeed?.Material != null)
        {
            try { activeNeedSatisfied = activeNeed.Material.amount + 0.0001 >= activeNeed.Desired; }
            catch { activeNeedSatisfied = false; }
        }

        var totals = new Dictionary<string, MaterialTotal>(StringComparer.Ordinal);
        // Keep standing stock only for opportunity windows that cannot be supplied reactively. The value model below
        // sizes quest-item supply exactly on demand.
        if (!Plugin.EndgameModeActive || !Plugin.Config.PermanentTimedCraftableEffectsEnabled.Value)
        {
            var whetstoneTargetUses = Plugin.Config.FillWhetstoneMaterialsToCapacity.Value
                ? int.MaxValue
                : Plugin.Config.TargetWhetstoneUses.Value;
            AddRequirements(totals, SmartItem.Whetstone, whetstoneTargetUses, 0);
            AddRequirements(totals, SmartItem.ShardsNecklace, Plugin.Config.TargetShardsNecklaceUses.Value, 3);
            AddRequirements(totals, SmartItem.SoulsCompass, Plugin.Config.TargetSoulsCompassUses.Value, 3);
            AddRequirements(totals, SmartItem.CriticalSword, Plugin.Config.TargetCriticalSwordUses.Value, 3);
            AddRequirements(totals, SmartItem.SoulsSpyglass, Plugin.Config.TargetSoulsSpyglassUses.Value, 3);
        }
        if (Plugin.Config.DedicatedRagePillMaterialFarming.Value)
            AddRequirements(totals, SmartItem.RagePill, Plugin.Config.TargetRagePillUses.Value, 1);

        MaterialNeed best = null;
        foreach (var total in totals.Values)
        {
            if (total.Material == null || total.Material.locked) continue;
            double capacity;
            try { capacity = total.Material.GetMaxAmount(); }
            catch { capacity = total.Material.maxAmount; }
            var desired = capacity > 0 ? Math.Min(total.Desired, capacity) : total.Desired;
            if (desired <= total.Material.amount + 0.0001) continue;
            var continuing = activeNeed != null && SameMaterial(activeNeed.Material, total.Material);
            var exactTrigger = total.ExactRefillStartAmount >= 0;
            var startAt = exactTrigger
                ? Math.Min(desired, total.ExactRefillStartAmount)
                : desired * Math.Clamp(total.Critical
                    ? Plugin.Config.CriticalMaterialRefillStartPercent.Value
                    : Plugin.Config.MaterialRefillStartPercent.Value, 0, 100) / 100.0;
        // Ordinary materials may fill opportunistically after a chain starts. Whetstone and Rage Pill must always honor
        // their exact trigger lines and cannot start early because a previous material just reached target.
            var aboveStart = exactTrigger
                ? total.Material.amount > startAt
                : total.Material.amount > startAt + 0.0001;
            if (!continuing && aboveStart && (exactTrigger || !activeNeedSatisfied)) continue;
            var materialsNeeded = Math.Max(0, desired - total.Material.amount);
            var efficiency = BestForMaterialCached(total.Material, materialsNeeded);
            var map = efficiency?.Map;
            if (map == null) continue; // Refined, quest, and minigame materials have no ordinary map drop; never travel blindly.
            var density = MapEfficiencyEstimator.EffectiveDensity(efficiency);
            var candidate = new MaterialNeed(total.Material, map, desired, total.Material.amount, total.Priority,
                density, efficiency.ExpectedEligibleKills, efficiency.TargetsGiant, total.Purpose);
            best = BetterNeed(best, candidate, mapController.selectedMap);
        }

            // Only Whetstone supply may preempt a critical material-enemy unlock; the current quest's own booster supply
            // still participates in value comparison.
        if (questPlan?.StrategicUnlockBonus > 0 && best?.Priority > 0) best = null;
            // Whetstone and Rage Pill stockouts have the highest opportunity cost and do not wait for ordinary quest supply.
        if (best?.Priority <= 1) return best;
            // An active composite route allows cross-map preemption only for top safety stock or value-positive supply
            // serving the current quest. Ordinary standing stock such as Compass or Necklace can refill on-route but
            // cannot pull the quest group away from its optimal map.
        if (questPlan?.TargetMap != null && BlocksIdleSurplusFarming(questPlan)) best = null;
        var selected = suppressQuestSupplies
            ? best
            : BetterNeed(best, FindQuestSupplyNeed(questPlan), mapController.selectedMap);
        // When a high-priority specified kill controls the map, Rage kills remain a parallel background task and must
        // enter the same value model. Otherwise execution requests pills while material planning sees only the primary.
        // Include only the current representative background Rage task; standing pill stock remains unchanged and does
        // not restore unlimited pill-material farming without a Rage quest.
        if (!suppressQuestSupplies && parallelRagePlan?.Quest != null &&
            parallelRagePlan.InstanceId != questPlan?.InstanceId && parallelRagePlan.Current < parallelRagePlan.Goal)
            selected = BetterNeed(selected, FindQuestSupplyNeed(parallelRagePlan), mapController.selectedMap);
        if (selected != null) return selected;
        var opportunistic = FindOpportunisticTaskMapNeed(questPlan);
        if (opportunistic != null) return opportunistic;
        // Do not insert lowest-priority surplus supply during Portal protection; material travel would interrupt a zero-cooldown Weekly.
        return suppressQuestSupplies || suppressIdleSurplus
            ? null
            : FindIdleSurplusMaterialNeed(questPlan, mapController);
    }

    private MaterialNeed FindIdleSurplusMaterialNeed(QuestPlan plan, MapController mapController)
    {
        if (!Plugin.Config.IdleSurplusMaterialFarming.Value ||
            (Plugin.EndgameModeActive && Plugin.Config.PermanentTimedCraftableEffectsEnabled.Value) ||
            mapController?.maps == null ||
            BlocksIdleSurplusFarming(plan)) return null;

        MaterialNeed best = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < mapController.maps.Length; i++)
        {
            var map = mapController.maps[i];
            if (!QuestDirector.IsStandardAvailableMap(map)) continue;
            foreach (var material in MapEfficiencyEstimator.CollectibleDrops(map))
            {
                if (material == null || material.locked) continue;
                var key = CraftableCatalog.Normalize(material.name);
                if (string.IsNullOrEmpty(key)) key = CraftableCatalog.Normalize(material.localizedName);
                if (string.IsNullOrEmpty(key))
                {
                    try { key = material.GetInstanceID().ToString(); }
                    catch { continue; }
                }
                if (!seen.Add(key)) continue;

                double capacity;
                try { capacity = material.GetMaxAmount(); }
                catch { capacity = material.maxAmount; }
                if (!double.IsFinite(capacity) || capacity <= 0 || material.amount + 0.0001 >= capacity)
                    continue;

                var missing = capacity - material.amount;
                var efficiency = BestForMaterialCached(material, missing);
                var density = MapEfficiencyEstimator.EffectiveDensity(efficiency);
                if (efficiency?.Map == null || density <= 0 ||
                    !double.IsFinite(efficiency.ExpectedEligibleKills)) continue;
                var candidate = new MaterialNeed(material, efficiency.Map, capacity, material.amount, 6,
                    density, efficiency.ExpectedEligibleKills, efficiency.TargetsGiant,
                    "Idle surplus material supply (fill to capacity)");
                best = BetterNeed(best, candidate, mapController.selectedMap);
            }
        }
        return best;
    }

    internal static bool BlocksIdleSurplusFarming(QuestPlan plan)
    {
        if (plan?.Quest == null) return false;
        if (plan.StrategicUnlockBonus > 0 || plan.TargetMap != null && plan.JointQuestCount > 0) return true;
        return plan.Type is QuestKind.KillEnemies or QuestKind.KillEnemiesWithArrows or QuestKind.KillFlyers or
            QuestKind.KillGiants or QuestKind.KillEnemiesWithRageMode or QuestKind.KillEnemiesWithCharacter or
            QuestKind.KillEnemiesOfType or QuestKind.KillEnemiesInLocation or QuestKind.KillShinyEnemies or QuestKind.WindDashKills or
            QuestKind.GetMaterials or QuestKind.GoThroughPortals or QuestKind.VisitLocation;
    }

    private static MaterialNeed FindOpportunisticTaskMapNeed(QuestPlan plan)
    {
        if (!Plugin.Config.OpportunisticTaskMapMaterialsEnabled.Value || plan?.TargetMap == null ||
            plan.Current >= plan.Goal) return null;
        MaterialNeed best = null;
        foreach (var material in MapEfficiencyEstimator.CollectibleDrops(plan.TargetMap))
        {
            if (material == null || material.locked) continue;
            double capacity;
            try { capacity = material.GetMaxAmount(); }
            catch { capacity = material.maxAmount; }
            if (!double.IsFinite(capacity) || capacity <= 0 || material.amount + 0.0001 >= capacity) continue;
            var missing = capacity - material.amount;
            var efficiency = MapEfficiencyEstimator.EvaluateForMaterial(plan.TargetMap, material, missing);
            var density = MapEfficiencyEstimator.EffectiveDensity(efficiency);
            if (density <= 0 || !double.IsFinite(efficiency.ExpectedEligibleKills)) continue;
            var candidate = new MaterialNeed(material, plan.TargetMap, capacity, material.amount, 5, density,
                efficiency.ExpectedEligibleKills, efficiency.TargetsGiant,
                $"Quest {plan.Name} opportunistic same-map supply (route unchanged)", plan.InstanceId, opportunistic: true);
            best = BetterNeed(best, candidate, plan.TargetMap);
        }
        return best;
    }

    private MaterialNeed FindQuestSupplyNeed(QuestPlan plan)
    {
        if (plan != null) _questRefineryRequests.Remove(plan.InstanceId);
        if (!Plugin.Config.QuestSupplyPlanningEnabled.Value || plan?.Quest == null ||
            !QuestApi.CanProgressNow(plan.Quest) || plan.Current >= plan.Goal)
            return null;
        // A low-progress Daily Rage kill is kept only when natural cooldown is nearly ready and one window suffices.
        // Supplying new pill or staff materials would violate that cost premise; persistent and Weekly quests still use
        // the full combined model.
        if (plan.Source == QuestSource.Daily && plan.Type == QuestKind.KillEnemiesWithRageMode)
            return null;

        var combinedStaffNeed = FindCombinedKillStaffSupplyNeed(plan);
        if (combinedStaffNeed != null) return combinedStaffNeed;

        QuestSupplyOption best = null;
        SmartItem firstRejectedKind = default;
        string firstRejection = null;
        foreach (var kind in RelevantQuestBoosters(plan))
        {
            if (kind == SmartItem.RagePill && plan.Type == QuestKind.KillEnemiesWithRageMode &&
                IsCombinedKillStaffQuest(plan))
                continue; // Already evaluated by the staff, cooldown, and one on-demand pill combined model.
            if (kind == SmartItem.RagePill && !Plugin.Config.DedicatedRagePillMaterialFarming.Value)
                continue;
            // Evaluate all three kill staffs as overlapping effects; single-item optimization causes split round trips.
            if (IsKillStaff(kind) && IsCombinedKillStaffQuest(plan)) continue;
            var item = _catalog.Get(kind);
            if (item == null) continue;
            var reserve = ReserveUses(kind);
            double craftable;
            double currentCoverage;
            bool itemActive;
            try
            {
                if (!CraftableCatalog.HasUnlockedRecipe(item)) continue;
                currentCoverage = PermanentClientBonuses.CoverageSeconds(item);
                itemActive = item.IsActive() || currentCoverage > 0.05;
                craftable = item.HowManyCanCraft();
                if (!double.IsFinite(craftable) || craftable < 0) continue;
            }
            catch { continue; }

            var boostedSeconds = itemActive
                ? plan.ExpectedSeconds
                : EstimateBoostedQuestSeconds(plan, kind, plan.ExpectedSeconds);
            var directSeconds = itemActive
                ? EstimateWithoutBoosterSeconds(plan, kind, boostedSeconds)
                : plan.ExpectedSeconds;
            if (!double.IsFinite(directSeconds) || boostedSeconds >= directSeconds) continue;

            var travel = MapEfficiencyEstimator.EstimatedTravelSeconds(plan.TargetMap);
            var boostedWorkSeconds = Math.Max(0, boostedSeconds - travel);
            var duration = ItemDuration(item);
            var plannedUses = PlannedAdditionalUses(kind, plan, boostedWorkSeconds, duration, currentCoverage);
            if (plannedUses <= 0) continue;

            var requestedStockUses = SaturatingAdd(reserve, plannedUses);
            var targetStockUses = CapStockUsesByMaterialCapacity(kind, item, requestedStockUses);
            var usableStockUses = Math.Max(0, targetStockUses - reserve);
            if (usableStockUses <= 0 || craftable + 0.0001 >= targetStockUses) continue;

            var baselineSeconds = duration > 0.01
                ? CoverageAdjustedSeconds(directSeconds, boostedSeconds, travel, currentCoverage)
                : directSeconds;
            var effectiveBoostedSeconds = duration > 0.01
                ? CoverageAdjustedSeconds(directSeconds, boostedSeconds, travel,
                    currentCoverage + usableStockUses * duration)
                : boostedSeconds;

            // Refinery recipes expand into ordinary raw inputs plus native queues. They are not
            // rejected merely because the refined output itself has no monster/drop map.
            var usesRefinedInputs = CraftSupplyPlanner.HasRefinedIngredients(item);
            if (usesRefinedInputs && !Plugin.Config.SupplementalRefiningEnabled.Value)
            { firstRejection ??= "Automatic refinery supply is disabled"; firstRejectedKind = kind; continue; }
            var pipeline = usesRefinedInputs
                ? _refinery.SupplyPlanner.Quote(item, targetStockUses, plan.TargetMap, 2,
                    plan.InstanceId, _refinery.ProtectedRawStock, taskItem: kind) : null;
            if (pipeline?.UsesRefinery == true)
            {
                if (!pipeline.Reachable) { firstRejection ??= pipeline.BlockedReason; firstRejectedKind = kind; continue; }
                if (pipeline.RawNeeds.Count > 0 && (!Plugin.Config.RefineryRawMaterialFarming.Value ||
                    !Plugin.Config.AutomaticMaterialTravel.Value))
                { firstRejection ??= "This refinery recipe needs raw-material travel, which is disabled"; firstRejectedKind = kind; continue; }
                var waiting = Math.Max(0, pipeline.ReadySeconds - pipeline.FarmingSeconds);
                var remainingFraction = baselineSeconds <= 0 ? 0 : Math.Clamp(1 - waiting / baselineSeconds, 0, 1);
                var coverageAfterSupply = Math.Max(0, currentCoverage - pipeline.ReadySeconds) + usableStockUses * duration;
                var afterSupply = CoverageAdjustedSeconds(directSeconds * remainingFraction,
                    boostedSeconds * remainingFraction, 0, coverageAfterSupply);
                var completion = pipeline.ReadySeconds + afterSupply;
                var saving = baselineSeconds - completion;
                if (saving < Math.Max(0, Plugin.Config.QuestSupplyMinimumNetSeconds.Value)) continue;
                var request = new RefineryRecipeRequest(item, targetStockUses, 0, Immediate: true,
                    DeadlineSeconds: baselineSeconds, AllowRawFarming: true, QuestPlanId: plan.InstanceId,
                    Purpose: $"Quest {plan.Name}: supply {pipeline.ReadySeconds:0}s, expected net saving {saving:0}s",
                    Task: plan.Quest, TaskItem: kind);
                _questRefineryRequests[plan.InstanceId] = request;
                // Waiting on an already-funded queue does not lock the map or stop natural quest progress.
                var rawNeed = pipeline.RawNeeds.OrderByDescending(n => MapEfficiencyEstimator.SameMap(n.Map,
                    MapController.instance?.selectedMap)).ThenByDescending(n => n.Severity).FirstOrDefault();
                if (rawNeed != null && (best == null || saving > best.NetSaving))
                    best = new QuestSupplyOption(kind, rawNeed, pipeline.ReadySeconds, afterSupply, baselineSeconds,
                        saving, plannedUses, usableStockUses, duration, targetStockUses < requestedStockUses);
                continue;
            }
            var needs = BuildSupplyNeeds(item, targetStockUses, kind, plan, out var unavailableReason);
            if (needs.Count == 0 || needs.Any(x => x.Need == null))
            {
                if (firstRejection == null)
                {
                    firstRejectedKind = kind;
                    firstRejection = unavailableReason ?? "Recipe has no refillable ordinary-material gap";
                }
                continue;
            }
            var farmSeconds = EstimateSupplyRouteSeconds(needs, plan.TargetMap);
            var netSaving = baselineSeconds - effectiveBoostedSeconds - farmSeconds;
            if (netSaving < Math.Max(0, Plugin.Config.QuestSupplyMinimumNetSeconds.Value) ||
                !StrategyMath.ShouldFarmForBooster(farmSeconds, baselineSeconds, effectiveBoostedSeconds))
            {
                if (firstRejection == null)
                {
                    firstRejectedKind = kind;
                    firstRejection = $"Expected supply {FormatDuration(farmSeconds)} + boosted completion {FormatDuration(effectiveBoostedSeconds)}, " +
                        $"direct completion {FormatDuration(baselineSeconds)}, net saving {FormatDuration(netSaving)}, below minimum net " +
                        $"{FormatDuration(Math.Max(0, Plugin.Config.QuestSupplyMinimumNetSeconds.Value))}";
                }
                continue;
            }

            var selected = needs.Where(x => x.Need != null)
                .OrderByDescending(x => MapEfficiencyEstimator.SameMap(x.Need.Map, MapController.instance?.selectedMap))
                .ThenByDescending(x => x.WorkSeconds)
                .ThenByDescending(x => x.Need.Severity)
                .First().Need;
            if (best == null || netSaving > best.NetSaving)
                best = new QuestSupplyOption(kind, selected, farmSeconds, effectiveBoostedSeconds, baselineSeconds,
                    netSaving, plannedUses, usableStockUses, duration, targetStockUses < requestedStockUses);
        }
        if (best == null)
        {
            if (firstRejection != null) LogQuestSupplySkip(plan, firstRejectedKind, firstRejection);
            return null;
        }
        var coverage = best.Duration > 0.01
            ? $"duration={best.Duration:0.#}s/use, planned={best.PlannedUses}, stocked={best.StockedUses}"
            : $"planned={best.PlannedUses}, stocked={best.StockedUses}";
        if (best.CapacityLimited) coverage += ", capacity-limited";
        SmartLog.Info(SmartLogCategory.Material,
            $"Quest supply selected: item={best.Kind}, {coverage}, first material={best.Need.Material?.name ?? "unknown"}. " +
            $"Farm {FormatDuration(best.FarmSeconds)} + boosted completion {FormatDuration(best.BoostedSeconds)} " +
            $"< direct completion {FormatDuration(best.DirectSeconds)}; net saving={FormatDuration(best.NetSaving)}.");
        return best.Need;
    }

    private MaterialNeed FindCombinedKillStaffSupplyNeed(QuestPlan plan)
    {
        BeginKillStaffRecommendation(plan);
        if (!IsCombinedKillStaffQuest(plan)) return null;

        var remaining = Math.Max(0, plan.Goal - plan.Current);
        if (remaining <= 0) return null;
        var rageQuest = plan.Type == QuestKind.KillEnemiesWithRageMode;
        var candidates = new List<KillStaffCandidate>();
        foreach (var definition in KillStaffDefinitions())
        {
            if (!QuestEffectPolicy.CanAffect(plan.Quest, definition.Kind)) continue;
            var item = _catalog.Get(definition.Kind);
            if (item == null) continue;
            try
            {
                if (!item.TabVisible() || !item.ExtraCondition()) continue;
                var coverage = PermanentClientBonuses.CoverageSeconds(item);
                var active = item.IsActive() || coverage > 0.05;
                var duration = ItemDuration(item);
                if (duration <= 0.01) continue;
                candidates.Add(new KillStaffCandidate(definition.Kind, definition.Bonus, item, active,
                    coverage, duration, ReserveUses(definition.Kind)));
            }
        catch { /* Exclude only the staff with invalid state. */ }
        }
        TemporaryCraftableItem ragePill = null;
        var ragePillTargetStock = 0;
        var canSupplyOneRagePill = false;
        if (rageQuest && QuestEffectPolicy.CanAffect(plan.Quest, SmartItem.RagePill) && Plugin.Config.QuestRagePillEnabled.Value &&
            (StrategyBehaviour.Current?.RemainingQuestRagePillAllowance(plan.InstanceId) ??
             Math.Max(0, Plugin.Config.MaximumRagePillsPerQuestSession.Value)) > 0)
        {
            ragePill = _catalog.Get(SmartItem.RagePill);
            try
            {
                if (ragePill != null && ragePill.TabVisible() && ragePill.ExtraCondition())
                {
                    ragePillTargetStock = Math.Max(0, Plugin.Config.RagePillReserveUses.Value) + 1;
                    var craftable = ragePill.HowManyCanCraft();
                    canSupplyOneRagePill = double.IsFinite(craftable) && craftable + 0.0001 < ragePillTargetStock &&
                                             CapStockUsesByMaterialCapacity(SmartItem.RagePill, ragePill, ragePillTargetStock) >= ragePillTargetStock &&
                                             StaffSupplyReachable(SmartItem.RagePill, ragePill, ragePillTargetStock);
                }
            }
            catch { canSupplyOneRagePill = false; }
        }
        if (candidates.Count == 0 && !canSupplyOneRagePill) return null;
        // An active lower staff cannot block supply for a higher staff. The old early exit let execution renew +10 forever
        // and starve +50. Treat active effects as expiring baseline value and allow future renewal supply for the same
        // staff, concatenating remaining and added duration without stacking the same multiplier twice.
        var activeCandidates = candidates.Where(x => x.Active || x.CurrentCoverage > 0.05).ToArray();

        var travel = MapEfficiencyEstimator.EstimatedTravelSeconds(plan.TargetMap);
        var killsPerMinute = MapEfficiencyEstimator.EstimatedTargetKillsPerMinute(
            plan.TargetEnemiesPerPattern, plan.TargetsGiants, includeTemporaryShards: false);
        var liveCredit = 1.0;
        try { liveCredit = Math.Max(1, PlayerInventory.instance?.questProgressPerKill ?? 1); }
        catch { /* Use one quest credit. */ }
        if (plan.UsesUnboostedProgress) liveCredit = 1;
        var activeBonus = activeCandidates.Sum(x => Math.Max(0, x.Bonus));
        var baseCredit = Math.Max(1, liveCredit - activeBonus);
        var directKillBonuses = ExistingKillBonusesAfter(activeCandidates, travel).ToArray();
        var directRageBonuses = ExistingRageBonusesAfter(activeCandidates, travel).ToArray();
        var directEstimate = KillQuestEstimator.Estimate(remaining, killsPerMinute, baseCredit,
            directKillBonuses);
        var actionsBefore = Math.Ceiling(directEstimate.KillActions);
        var directSeconds = travel + directEstimate.Seconds;
        if (rageQuest)
        {
            var runtime = RageQuestEstimator.Capture(_catalog, plan.InstanceId, travel);
            var direct = RageQuestEstimator.EstimateKill(remaining, killsPerMinute, baseCredit, runtime,
                directRageBonuses);
            directSeconds = travel + direct.Seconds;
            actionsBefore = Math.Ceiling(direct.KillActions);
        }
        KillStaffBatchOption best = null;
        string bestRejection = null;
        SmartItem rejectedKind = default;
        var bestRejectedNet = double.NegativeInfinity;

        // There are at most three staffs. Enumerate overlapping subsets and each subset's continuous coverage within
        // material capacity; large quests are not limited to one staff window.
        var subsetCount = 1 << candidates.Count;
        var pillVariants = canSupplyOneRagePill ? 2 : 1;
        var maximumUsefulCoverage = Math.Max(1, directSeconds - travel);
        for (var pillVariant = 0; pillVariant < pillVariants; pillVariant++)
        {
            var includeRagePill = pillVariant == 1;
            for (var mask = 0; mask < subsetCount; mask++)
            {
                if (mask == 0 && !includeRagePill) continue;
                var selectedCandidates = new List<KillStaffCandidate>();
                for (var i = 0; i < candidates.Count; i++)
                {
                    if ((mask & (1 << i)) != 0) selectedCandidates.Add(candidates[i]);
                }

                foreach (var stockPlans in EnumerateKillStaffCoveragePlans(selectedCandidates,
                             maximumUsefulCoverage))
                {
                    if (!TryResolveKillStaffSupplyPlan(stockPlans, plan,
                            includeRagePill ? ragePill : null, includeRagePill ? ragePillTargetStock : 0,
                            travel, out var resolvedStockPlans, out var needs, out var farmSeconds,
                            out var preparationSeconds, out var capacityLimited, out var unavailableReason))
                    {
                        if (bestRejection == null)
                        {
                            rejectedKind = resolvedStockPlans.Count > 0
                                ? resolvedStockPlans[0].Candidate.Kind : SmartItem.RagePill;
                            bestRejection = unavailableReason ?? "The full recipe materials for this staff or pill batch cannot be carried together";
                        }
                        continue;
                    }
                    if (resolvedStockPlans.Count == 0 && !includeRagePill) continue;

                    // A zero-supply candidate still participates in global optimization. Skipping it selected the first
                    // larger count with a real gap and supplied four staffs merely to create that gap. Share execution's
                    // marginal-kill threshold and distinguish renewal from first activation at preparation completion,
                    // avoiding supply for a craft execution would reject.
                    if (!KillStaffPlanMatchesExecution(resolvedStockPlans, remaining, baseCredit,
                            activeCandidates, preparationSeconds)) continue;
                    double boostedSeconds;
                    double actionsAfter;
                    if (rageQuest)
                    {
                        var runtime = RageQuestEstimator.Capture(_catalog, plan.InstanceId, preparationSeconds,
                            includeRagePill ? 1 : 0);
                        var timedBonuses = CombinedRageBonusesAfter(activeCandidates, resolvedStockPlans,
                            preparationSeconds).ToArray();
                        var boosted = RageQuestEstimator.EstimateKill(remaining, killsPerMinute, baseCredit,
                            runtime, timedBonuses);
                        boostedSeconds = boosted.Seconds + (needs.Count == 0 ? travel : 0);
                        actionsAfter = Math.Ceiling(boosted.KillActions);
                    }
                    else
                    {
                        var bonuses = CombinedKillBonusesAfter(activeCandidates, resolvedStockPlans,
                            preparationSeconds).ToArray();
                        var boosted = KillQuestEstimator.Estimate(remaining, killsPerMinute, baseCredit, bonuses);
                        // A supply route already includes return travel to the quest map and must not count original travel twice.
                        // A zero-supply candidate still starts on the current map and retains original travel time.
                        boostedSeconds = boosted.Seconds + (needs.Count == 0 ? travel : 0);
                        actionsAfter = Math.Ceiling(boosted.KillActions);
                    }
                    var netSaving = directSeconds - boostedSeconds - farmSeconds;
                    var enoughActionsSaved = resolvedStockPlans.Count == 0 || actionsBefore - actionsAfter >=
                                             Math.Max(1, Plugin.Config.MinimumKillActionsSavedByStaff.Value);
                    var minimumNet = Math.Max(0, Plugin.Config.QuestSupplyMinimumNetSeconds.Value);
                    if (!enoughActionsSaved || netSaving < minimumNet ||
                        !StrategyMath.ShouldFarmForBooster(farmSeconds, directSeconds, boostedSeconds))
                    {
                        if (netSaving > bestRejectedNet)
                        {
                            bestRejectedNet = netSaving;
                            rejectedKind = resolvedStockPlans.Count > 0
                                ? resolvedStockPlans.OrderByDescending(x => x.Candidate.Bonus).First().Candidate.Kind
                                : SmartItem.RagePill;
                            bestRejection = $"Continuous coverage expects supply {FormatDuration(farmSeconds)} + completion {FormatDuration(boostedSeconds)}, " +
                                $"direct completion {FormatDuration(directSeconds)}, net saving {FormatDuration(netSaving)}, " +
                                $"minimum required {FormatDuration(minimumNet)}";
                        }
                        continue;
                    }

                    if (best == null || netSaving > best.NetSaving)
                        best = new KillStaffBatchOption(resolvedStockPlans, needs, farmSeconds, boostedSeconds, directSeconds,
                            netSaving, actionsBefore, actionsAfter, includeRagePill);
                }
            }
        }

        if (best == null)
        {
            if (bestRejection != null) LogQuestSupplySkip(plan, rejectedKind, bestRejection);
            return null;
        }

        CommitKillStaffRecommendation(plan, best.StockPlans);

        // Do not create material travel when the optimal plan is already stocked. Quest execution crafts once and
        // continues only when coverage is insufficient and the quest remains incomplete.
        if (best.Needs.Count == 0) return null;

        var selected = best.Needs.Where(x => x.Need != null)
            .OrderByDescending(x => MapEfficiencyEstimator.SameMap(x.Need.Map, MapController.instance?.selectedMap))
            .ThenByDescending(x => x.WorkSeconds)
            .ThenByDescending(x => x.Need.Severity)
            .First().Need;
        var summaryParts = best.StockPlans.Select(x => $"{x.Candidate.Kind} x{x.PlannedUses}").ToList();
        if (best.IncludesRagePill) summaryParts.Add("RagePill x1 on demand");
        var summary = string.Join(" + ", summaryParts);
        LogCombinedStaffSupply(plan, selected, summary, best.ActionsBefore, best.ActionsAfter, best.FarmSeconds,
            best.BoostedSeconds, best.DirectSeconds, best.NetSaving, false, best.IncludesRagePill);
        return selected;
    }

    internal bool ShouldCraftRecommendedKillStaff(QuestPlan plan, SmartItem kind)
    {
        if (plan?.Quest == null || !QuestEffectPolicy.CanSpend(plan.Quest, kind) ||
            !_recommendedKillStaffUsesByQuest.TryGetValue(plan.InstanceId, out var recommendations)) return false;
        return recommendations.TryGetValue(kind, out var uses) && uses > 0;
    }

    internal bool HasRecommendedKillStaff(QuestPlan plan)
    {
        if (plan?.Quest == null || plan.UsesUnboostedProgress || !QuestApi.CanProgressNow(plan.Quest) ||
            !_recommendedKillStaffUsesByQuest.TryGetValue(plan.InstanceId, out var recommendations)) return false;
        return recommendations.Any(pair => pair.Value > 0 && QuestEffectPolicy.CanSpend(plan.Quest, pair.Key));
    }

    private void BeginKillStaffRecommendation(QuestPlan plan)
    {
        var id = plan?.InstanceId ?? 0;
        if (id == 0) return;
        _recommendedKillStaffUsesByQuest[id] = new Dictionary<SmartItem, int>();
    }

    private void CommitKillStaffRecommendation(QuestPlan plan, IEnumerable<KillStaffStockPlan> plans)
    {
        var id = plan?.InstanceId ?? 0;
        if (id == 0) return;
        if (!_recommendedKillStaffUsesByQuest.TryGetValue(id, out var recommendations))
        {
            recommendations = new Dictionary<SmartItem, int>();
            _recommendedKillStaffUsesByQuest[id] = recommendations;
        }
        recommendations.Clear();
        if (plans != null)
        {
            foreach (var stock in plans)
            {
                if (stock?.Candidate == null || stock.PlannedUses <= 0) continue;
                recommendations[stock.Candidate.Kind] = stock.PlannedUses;
            }
        }
    }

    private static IEnumerable<KillQuestTimedBonus> ExistingKillBonusesAfter(
        IEnumerable<KillStaffCandidate> activeCandidates, double preparationSeconds) =>
        activeCandidates.Select(x => new KillQuestTimedBonus(x.Bonus,
                Math.Max(0, x.CurrentCoverage - Math.Max(0, preparationSeconds))))
            .Where(x => x.DurationSeconds > 0.0001);

    private static IEnumerable<RageQuestTimedBonus> ExistingRageBonusesAfter(
        IEnumerable<KillStaffCandidate> activeCandidates, double preparationSeconds) =>
        activeCandidates.Select(x => new RageQuestTimedBonus(x.Bonus,
                Math.Max(0, x.CurrentCoverage - Math.Max(0, preparationSeconds))))
            .Where(x => x.DurationSeconds > 0.0001);

    private static IEnumerable<KillQuestTimedBonus> CombinedKillBonusesAfter(
        IReadOnlyList<KillStaffCandidate> activeCandidates, IReadOnlyList<KillStaffStockPlan> stockPlans,
        double preparationSeconds)
    {
        var activeKinds = new HashSet<SmartItem>();
        for (var i = 0; i < activeCandidates.Count; i++)
        {
            var active = activeCandidates[i];
            activeKinds.Add(active.Kind);
            var duration = Math.Max(0, active.CurrentCoverage - Math.Max(0, preparationSeconds));
            var extension = stockPlans.FirstOrDefault(x => x.Candidate.Kind == active.Kind);
            if (extension != null) duration += extension.Candidate.Duration * extension.PlannedUses;
            if (duration > 0.0001) yield return new KillQuestTimedBonus(active.Bonus, duration);
        }
        for (var i = 0; i < stockPlans.Count; i++)
        {
            var stock = stockPlans[i];
            if (activeKinds.Contains(stock.Candidate.Kind)) continue;
            var duration = stock.Candidate.Duration * stock.PlannedUses;
            if (duration > 0.0001) yield return new KillQuestTimedBonus(stock.Candidate.Bonus, duration);
        }
    }

    private static IEnumerable<RageQuestTimedBonus> CombinedRageBonusesAfter(
        IReadOnlyList<KillStaffCandidate> activeCandidates, IReadOnlyList<KillStaffStockPlan> stockPlans,
        double preparationSeconds)
    {
        var activeKinds = new HashSet<SmartItem>();
        for (var i = 0; i < activeCandidates.Count; i++)
        {
            var active = activeCandidates[i];
            activeKinds.Add(active.Kind);
            var duration = Math.Max(0, active.CurrentCoverage - Math.Max(0, preparationSeconds));
            var extension = stockPlans.FirstOrDefault(x => x.Candidate.Kind == active.Kind);
            if (extension != null) duration += extension.Candidate.Duration * extension.PlannedUses;
            if (duration > 0.0001) yield return new RageQuestTimedBonus(active.Bonus, duration);
        }
        for (var i = 0; i < stockPlans.Count; i++)
        {
            var stock = stockPlans[i];
            if (activeKinds.Contains(stock.Candidate.Kind)) continue;
            var duration = stock.Candidate.Duration * stock.PlannedUses;
            if (duration > 0.0001) yield return new RageQuestTimedBonus(stock.Candidate.Bonus, duration);
        }
    }

    private IEnumerable<List<KillStaffStockPlan>> EnumerateKillStaffCoveragePlans(
        List<KillStaffCandidate> selectedCandidates, double maximumUsefulCoverage)
    {
        if (selectedCandidates == null || selectedCandidates.Count == 0)
        {
            yield return new List<KillStaffStockPlan>();
            yield break;
        }

        var maximumUses = new int[selectedCandidates.Count];
        var coverageBreakpoints = new SortedSet<double>();
        for (var i = 0; i < selectedCandidates.Count; i++)
        {
            var candidate = selectedCandidates[i];
            var totalCapacityUses = CapStockUsesByMaterialCapacity(candidate.Kind, candidate.Item, int.MaxValue);
            var materialLimitedUses = Math.Max(0, totalCapacityUses - candidate.ReserveUses);
            var usefulUses = Math.Max(1, CeilToInt(maximumUsefulCoverage / candidate.Duration));
            maximumUses[i] = Math.Min(materialLimitedUses, usefulUses);
            if (maximumUses[i] <= 0 ||
                !StaffSupplyReachable(candidate.Kind, candidate.Item, SaturatingAdd(candidate.ReserveUses, 1)))
                yield break;

        // Every staff-duration boundary changes whether another craft is worthwhile. These points cover every exact
        // single-staff count and enumerate continuous multi-staff coverage across differing durations.
            for (var uses = 1; uses <= maximumUses[i]; uses++)
                coverageBreakpoints.Add(Math.Min(maximumUsefulCoverage, candidate.Duration * uses));
        // An active staff usually has partial duration remaining. Also enumerate zero-supply plans of remaining time plus
        // N stocked uses; otherwise 116 seconds plus five uses rounds to six and triggers a trip for a small coverage gap.
            if (candidate.CurrentCoverage > 0.05)
            {
                coverageBreakpoints.Add(Math.Min(maximumUsefulCoverage, candidate.CurrentCoverage));
                for (var uses = 1; uses <= maximumUses[i]; uses++)
                    coverageBreakpoints.Add(Math.Min(maximumUsefulCoverage,
                        candidate.CurrentCoverage + candidate.Duration * uses));
            }
        }

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var targetCoverage in coverageBreakpoints)
        {
            var plans = new List<KillStaffStockPlan>(selectedCandidates.Count);
            var keyParts = new string[selectedCandidates.Count];
            var reachable = true;
            for (var i = 0; i < selectedCandidates.Count; i++)
            {
                var candidate = selectedCandidates[i];
                var plannedUses = Math.Min(maximumUses[i],
                    Math.Max(1, CeilToInt(targetCoverage / candidate.Duration)));
                var targetStockUses = SaturatingAdd(candidate.ReserveUses, plannedUses);
                if (!StaffSupplyReachable(candidate.Kind, candidate.Item, targetStockUses))
                {
                    reachable = false;
                    break;
                }
                plans.Add(new KillStaffStockPlan(candidate, plannedUses, targetStockUses,
                    targetCoverage, false));
                keyParts[i] = plannedUses.ToString();
            }
            if (!reachable || !emitted.Add(string.Join("/", keyParts))) continue;
            yield return plans;
        }
    }

    private bool TryResolveKillStaffSupplyPlan(List<KillStaffStockPlan> coveragePlans, QuestPlan plan,
        TemporaryCraftableItem ragePill, int ragePillTargetStock, double travelSeconds,
        out List<KillStaffStockPlan> resolvedPlans, out List<SupplyNeedEstimate> needs,
        out double farmSeconds, out double preparationSeconds, out bool capacityLimited,
        out string unavailableReason)
    {
        resolvedPlans = AdjustKillStaffPlansForPreparation(coveragePlans, travelSeconds);
        needs = new List<SupplyNeedEstimate>();
        farmSeconds = 0;
        preparationSeconds = travelSeconds;
        capacityLimited = false;
        unavailableReason = null;

        // Material gaps determine supply time, which consumes active staff duration. Iterate until planned uses stabilize,
        // normally in one or two rounds; the cap defends only against abnormal material or travel data.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            needs = BuildCombinedStaffSupplyNeeds(resolvedPlans, plan, ragePill, ragePillTargetStock,
                out capacityLimited, out unavailableReason);
            if (capacityLimited || needs.Any(x => x.Need == null)) return false;

            farmSeconds = needs.Count == 0 ? 0 : EstimateSupplyRouteSeconds(needs, plan.TargetMap);
            preparationSeconds = needs.Count == 0 ? travelSeconds : farmSeconds;
            var adjusted = AdjustKillStaffPlansForPreparation(coveragePlans, preparationSeconds);
            if (SameKillStaffUses(resolvedPlans, adjusted)) return true;
            resolvedPlans = adjusted;
        }

        unavailableReason = "Staff remaining duration and supply route did not stabilize within the safe iteration limit";
        return false;
    }

    private static List<KillStaffStockPlan> AdjustKillStaffPlansForPreparation(
        IEnumerable<KillStaffStockPlan> coveragePlans, double preparationSeconds)
    {
        var result = new List<KillStaffStockPlan>();
        var preparation = Math.Max(0, preparationSeconds);
        foreach (var coverage in coveragePlans)
        {
            var remainingCoverage = Math.Max(0, coverage.Candidate.CurrentCoverage - preparation);
            var uncovered = Math.Max(0, coverage.TargetCoverage - remainingCoverage);
            var plannedUses = Math.Min(coverage.PlannedUses,
                Math.Max(0, CeilToInt(uncovered / coverage.Candidate.Duration)));
            if (plannedUses <= 0) continue;
            result.Add(new KillStaffStockPlan(coverage.Candidate, plannedUses,
                SaturatingAdd(coverage.Candidate.ReserveUses, plannedUses), coverage.TargetCoverage,
                coverage.CapacityLimited));
        }
        return result;
    }

    private static bool SameKillStaffUses(IReadOnlyList<KillStaffStockPlan> left,
        IReadOnlyList<KillStaffStockPlan> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
            if (left[i].Candidate.Kind != right[i].Candidate.Kind ||
                left[i].PlannedUses != right[i].PlannedUses) return false;
        return true;
    }

    private static bool IsCombinedKillStaffQuest(QuestPlan plan) => plan != null &&
        (QuestEffectPolicy.CanAffect(plan.Quest, SmartItem.AlternateDimensionStaff) ||
         plan.Type == QuestKind.KillEnemiesWithRageMode && QuestEffectPolicy.CanAffect(plan.Quest, SmartItem.RagePill));

    private static bool IsKillStaff(SmartItem kind) => CraftableEffectModel.IsKillStaff(kind);

    private static bool KillStaffPlanMatchesExecution(IEnumerable<KillStaffStockPlan> plans,
        double remaining, double baseCredit, IReadOnlyList<KillStaffCandidate> activeCandidates,
        double preparationSeconds)
    {
        var currentCredit = Math.Max(1, baseCredit + activeCandidates
            .Where(x => x.CurrentCoverage - Math.Max(0, preparationSeconds) > 0.05)
            .Sum(x => Math.Max(0, x.Bonus)));
        foreach (var plan in plans)
        {
            var stillActive = plan.Candidate.CurrentCoverage - Math.Max(0, preparationSeconds) > 0.05;
            if (stillActive)
            {
                if (Math.Ceiling(remaining / currentCredit) <
                    StrategyMath.KillStaffMinimumActionsSaved(plan.Candidate.Kind)) return false;
                continue;
            }
            if (!StrategyMath.KillStaffSavesEnough(remaining, currentCredit,
                    plan.Candidate.Kind, plan.Candidate.Bonus)) return false;
            currentCredit += plan.Candidate.Bonus;
        }
        return true;
    }

    private static IEnumerable<(SmartItem Kind, int Bonus)> KillStaffDefinitions()
    {
        foreach (var staff in CraftableEffectModel.KillStaffsAscending()) yield return staff;
    }

    private bool StaffSupplyReachable(SmartItem kind, TemporaryCraftableItem item, int requiredUses)
    {
        try
        {
            var requirements = item.GetRequirements();
            if (requirements == null) return false;
            for (var i = 0; i < requirements.Length; i++)
            {
                var requirement = requirements[i];
                var material = requirement?.material;
                if (material == null || requirement.divinityPoints || requirement.amount <= 0) continue;
                double capacity;
                try { capacity = material.GetMaxAmount(); }
                catch { capacity = material.maxAmount; }
                var stock = requirement.amount * (double)Math.Max(1, requiredUses);
                var plannedUses = Math.Max(1, requiredUses - ReserveUses(kind));
                var desired = MaterialReservePolicy.RequiredStock(material, stock,
                    requirement.amount * (double)plannedUses);
                if (double.IsFinite(capacity) && capacity > 0) desired = Math.Min(desired, capacity);
                var missing = Math.Max(0, desired - material.amount);
                if (missing <= 0.0001) continue;
                if (material.locked) return false;
                var efficiency = BestForMaterialCached(material, missing);
                if (efficiency?.Map == null || MapEfficiencyEstimator.EffectiveDensity(efficiency) <= 0 ||
                    !double.IsFinite(efficiency.ExpectedEligibleKills)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private List<SupplyNeedEstimate> BuildCombinedStaffSupplyNeeds(List<KillStaffStockPlan> plans,
        QuestPlan plan, TemporaryCraftableItem ragePill, int ragePillTargetStock,
        out bool capacityLimited, out string unavailableReason)
    {
        capacityLimited = plans.Any(x => x.CapacityLimited);
        unavailableReason = null;
        var totals = new Dictionary<int, CombinedStaffMaterial>();
        try
        {
            foreach (var stock in plans)
            {
                var requirements = stock.Candidate.Item.GetRequirements();
                if (requirements == null) continue;
                for (var i = 0; i < requirements.Length; i++)
                {
                    var requirement = requirements[i];
                    var material = requirement?.material;
                    if (material == null || requirement.divinityPoints || requirement.amount <= 0) continue;
                    var id = material.GetInstanceID();
                    if (!totals.TryGetValue(id, out var total))
                    {
                        total = new CombinedStaffMaterial(material);
                        totals[id] = total;
                    }
                    total.Desired += requirement.amount * (double)Math.Max(1, stock.TargetStockUses);
                    total.PlannedConsumption += requirement.amount * (double)Math.Max(0, stock.PlannedUses);
                    total.Items.Add(stock.Candidate.Kind);
                }
            }
            if (ragePill != null && ragePillTargetStock > 0)
            {
                var requirements = ragePill.GetRequirements();
                if (requirements != null)
                {
                    for (var i = 0; i < requirements.Length; i++)
                    {
                        var requirement = requirements[i];
                        var material = requirement?.material;
                        if (material == null || requirement.divinityPoints || requirement.amount <= 0) continue;
                        var id = material.GetInstanceID();
                        if (!totals.TryGetValue(id, out var total))
                        {
                            total = new CombinedStaffMaterial(material);
                            totals[id] = total;
                        }
            // A Rage quest supplies reserve plus the one pill needed now; it never restores global pill stock.
                        total.Desired += requirement.amount * (double)ragePillTargetStock;
                        total.PlannedConsumption += requirement.amount;
                        total.Items.Add(SmartItem.RagePill);
                    }
                }
            }

            var result = new List<SupplyNeedEstimate>();
            foreach (var total in totals.Values)
            {
                double capacity;
                try { capacity = total.Material.GetMaxAmount(); }
                catch { capacity = total.Material.maxAmount; }
                var desired = MaterialReservePolicy.RequiredStock(total.Material, total.Desired,
                    total.PlannedConsumption);
                if (double.IsFinite(capacity) && capacity > 0 && desired > capacity)
                {
                    capacityLimited = true;
                    unavailableReason ??= $"Material {DisplayName(total.Material)} needs {desired:0} but capacity is {capacity:0}; the combination cannot fit one window";
                    result.Add(new SupplyNeedEstimate(null, double.PositiveInfinity));
                    continue;
                }
                var missing = Math.Max(0, desired - total.Material.amount);
                if (missing <= 0.0001) continue;
                if (total.Material.locked)
                {
                    unavailableReason ??= $"Material {DisplayName(total.Material)} is locked";
                    result.Add(new SupplyNeedEstimate(null, double.PositiveInfinity));
                    continue;
                }
                var efficiency = BestForMaterialCached(total.Material, missing);
                var density = MapEfficiencyEstimator.EffectiveDensity(efficiency);
                if (efficiency?.Map == null || density <= 0 || !double.IsFinite(efficiency.ExpectedEligibleKills))
                {
                    unavailableReason ??= $"Material {DisplayName(total.Material)} has no unlocked automatically killable drop enemy or map";
                    result.Add(new SupplyNeedEstimate(null, double.PositiveInfinity));
                    continue;
                }
                var expectedKills = ExpectedSupplyMaterialKills(missing, efficiency.TargetsGiant,
                    projectWhetstone: true);
                var workSeconds = expectedKills / MapEfficiencyEstimator.EstimatedTargetKillsPerMinute(
                    density, efficiency.TargetsGiant, includeTemporaryShards: false) * 60;
                var purpose = ragePill != null
                    ? $"Quest {plan.Name} combined supply: staffs, cooldown, and one on-demand Rage Pill"
                    : $"Quest {plan.Name} combined supply: overlapping dimension staffs";
                var need = new MaterialNeed(total.Material, efficiency.Map, desired, total.Material.amount, 2,
                    density, expectedKills, efficiency.TargetsGiant, purpose, plan.InstanceId, questItems: total.Items);
                result.Add(new SupplyNeedEstimate(need, workSeconds));
            }
            return result;
        }
        catch
        {
            unavailableReason = "Unable to read combined staff recipes or material state";
            return new List<SupplyNeedEstimate> { new(null, double.PositiveInfinity) };
        }
    }

    private void LogCombinedStaffSupply(QuestPlan plan, MaterialNeed selected, string summary,
        double actionsBefore, double actionsAfter, double farmSeconds, double boostedSeconds,
        double directSeconds, double netSaving, bool capacityLimited, bool includesRagePill)
    {
        var now = UnityEngine.Time.unscaledTime;
        // Primary and background Rage quests may alternate recalculation. One shared last-key oscillated and logged both
        // every 0.5 seconds. Throttle independently per quest so plan changes report at most every 30 seconds without
        // affecting calculation or execution.
        if (_nextCombinedStaffLogAtByQuest.TryGetValue(plan.InstanceId, out var nextLogAt) && now < nextLogAt)
            return;
        _nextCombinedStaffLogAtByQuest[plan.InstanceId] = now + 30f;
        if (_nextCombinedStaffLogAtByQuest.Count > 256)
            foreach (var id in _nextCombinedStaffLogAtByQuest.Where(x => x.Value < now).Select(x => x.Key).ToArray())
                _nextCombinedStaffLogAtByQuest.Remove(id);
        SmartLog.Info(SmartLogCategory.Material,
            $"Combined quest supply: {summary}; " +
            $"{(includesRagePill ? "staff duration, Rage cooldown, and one on-demand pill share one model" : "staff effects can overlap")}; " +
            $"real kills {actionsBefore:0} -> {actionsAfter:0}; first material={selected.Material?.name ?? "unknown"}. " +
            $"Farm {FormatDuration(farmSeconds)} + complete {FormatDuration(boostedSeconds)} " +
            $"< no-staff completion {FormatDuration(directSeconds)}; net saving={FormatDuration(netSaving)}" +
            $"{(CanProjectWhetstoneBoost() ? "; farm time includes the Whetstone +65% drop chance" : string.Empty)}" +
            $"{(capacityLimited ? "; material capacity was included in the combination choice" : string.Empty)}.");
    }

    private static int PlannedAdditionalUses(SmartItem kind, QuestPlan plan, double boostedWorkSeconds,
        double duration, double currentCoverage)
    {
        if (kind == SmartItem.RagePill)
        {
            var freeActivation = 0;
            try
            {
                var rage = RageModeManager.instance;
                freeActivation = rage != null && (rage.currentState != RageModeManager.RageModeStates.NotActive ||
                    rage.currentCd <= 0.01) ? 1 : 0;
                var rageDuration = Math.Max(1, rage?.GetDuration() ?? 20);
                if (plan.Type == QuestKind.UseRageMode)
                    return Math.Max(0, plan.Goal - plan.Current - freeActivation);
                return Math.Max(0, CeilToInt(boostedWorkSeconds / rageDuration) - freeActivation);
            }
            catch { return 1; }
        }

        // Instant items such as Specialization and Key Manifest need one use; timed items supply the full boosted quest duration.
        if (duration <= 0.01) return 1;
        return CeilToInt(Math.Max(0, boostedWorkSeconds - currentCoverage) / duration);
    }

    private double EstimateWithoutBoosterSeconds(QuestPlan plan, SmartItem kind,
        double fullBoostedSeconds)
    {
        var travel = MapEfficiencyEstimator.EstimatedTravelSeconds(plan.TargetMap);
        var boostedWork = Math.Max(0, fullBoostedSeconds - travel);
        if (boostedWork <= 0) return fullBoostedSeconds;
        var inventory = PlayerInventory.instance;
        var multiplier = kind switch
        {
            SmartItem.CriticalSword => CraftableYieldModel.CriticalQuestSpeedRatio(_catalog.Get(kind), removeActive: true),
            SmartItem.DeathwaveScepter or SmartItem.BidimensionalStaff or SmartItem.AlternateDimensionStaff =>
                RemovalMultiplier(inventory?.questProgressPerKill ?? 1,
                    CraftableEffectModel.KillProgressBonus(kind)),
            SmartItem.ShardsNecklace => GiantSpawnRemovalMultiplier(),
            SmartItem.NecklaceOfCollectables => RemovalMultiplier(
                inventory?.collectablesQuestsProgress ?? 1,
                CraftableEffectModel.CollectablesQuestProgressBonus),
            SmartItem.RandomBoxStaff => RemovalMultiplier(
                inventory?.randomBoxQuestsProgress ?? 1,
                CraftableEffectModel.RandomBoxQuestProgressBonus),
            SmartItem.RandomStone => CraftableEffectModel.RandomStoneProgressMultiplier,
            SmartItem.Whetstone => MaterialBoostRemovalMultiplier(inventory),
            SmartItem.RagePill => CraftableEffectModel.RagePillPlanningMultiplier,
            _ => 1
        };
        return travel + boostedWork * Math.Max(1, multiplier);
    }

    private static double RemovalMultiplier(double liveCredit, double bonus)
    {
        var without = Math.Max(1, liveCredit - bonus);
        return Math.Max(1, liveCredit / without);
    }

    private static double MaterialBoostRemovalMultiplier(PlayerInventory inventory)
    {
        if (inventory == null) return 1;
        try
        {
            var boosted = StrategyMath.PercentageToProbability(inventory.dropChance);
            var without = StrategyMath.PercentageToProbability(Math.Max(0, inventory.dropChance - 65));
            return without <= 0 ? 1 : Math.Max(1, boosted / without);
        }
        catch { return 1; }
    }

    private static double GiantSpawnRemovalMultiplier()
    {
        var boosted = MapEfficiencyEstimator.CurrentGiantSpawnFactor(includeTemporaryShards: true);
        var without = MapEfficiencyEstimator.CurrentGiantSpawnFactor(includeTemporaryShards: false);
        return Math.Max(1, boosted / Math.Max(1, without));
    }

    private static double ItemDuration(TemporaryCraftableItem item)
    {
        if (item == null) return 0;
        try { return Math.Max(0, item.GetDuration()); }
        catch
        {
            try { return Math.Max(0, item.duration); }
            catch { return 0; }
        }
    }

    private static int CapStockUsesByMaterialCapacity(SmartItem kind, TemporaryCraftableItem item, int requestedUses)
    {
        var capped = Math.Max(0, requestedUses);
        var itemReserveUses = ReserveUses(kind);
        try
        {
            var requirements = item.GetRequirements();
            if (requirements == null) return capped;
            for (var i = 0; i < requirements.Length; i++)
            {
                var requirement = requirements[i];
                var material = requirement?.material;
                if (material == null || requirement.divinityPoints || requirement.amount <= 0) continue;
                double capacity;
                try { capacity = material.GetMaxAmount(); }
                catch { capacity = material.maxAmount; }
                if (!double.IsFinite(capacity) || capacity <= 0) continue;
                var recipeCapacity = Math.Max(0, CeilToInt(Math.Floor(capacity / requirement.amount)));
                var rawReserve = MaterialReservePolicy.MinimumReserve(material);
                var plannedCapacity = Math.Max(0,
                    CeilToInt(Math.Floor(Math.Max(0, capacity - rawReserve) / requirement.amount)));
                capped = Math.Min(capped, Math.Min(recipeCapacity,
                    SaturatingAdd(itemReserveUses, plannedCapacity)));
            }
        }
        catch { return Math.Min(capped, 1); }
        return capped;
    }

    private static double CoverageAdjustedSeconds(double directSeconds, double fullBoostedSeconds,
        double travelSeconds, double availableBoostedCoverage)
    {
        var directWork = Math.Max(0, directSeconds - travelSeconds);
        var boostedWork = Math.Max(0, fullBoostedSeconds - travelSeconds);
        if (directWork <= 0 || boostedWork <= 0 || availableBoostedCoverage >= boostedWork)
            return fullBoostedSeconds;
        var covered = Math.Clamp(availableBoostedCoverage, 0, boostedWork);
        var completedFraction = covered / boostedWork;
        return travelSeconds + covered + directWork * (1 - completedFraction);
    }

    private static int SaturatingAdd(int left, int right) =>
        left >= int.MaxValue - right ? int.MaxValue : left + right;

    private static int CeilToInt(double value)
    {
        if (!double.IsFinite(value) || value >= int.MaxValue) return int.MaxValue;
        if (value <= 0) return 0;
        return (int)Math.Ceiling(value);
    }

    private List<SupplyNeedEstimate> BuildSupplyNeeds(TemporaryCraftableItem item, int requiredUses,
        SmartItem kind, QuestPlan plan, out string unavailableReason)
    {
        unavailableReason = null;
        var result = new List<SupplyNeedEstimate>();
        try
        {
            var requirements = item.GetRequirements();
            if (requirements == null) return result;
            for (var i = 0; i < requirements.Length; i++)
            {
                var requirement = requirements[i];
                var material = requirement?.material;
                if (material == null || requirement.divinityPoints || requirement.amount <= 0) continue;
                double capacity;
                try { capacity = material.GetMaxAmount(); }
                catch { capacity = material.maxAmount; }
                var stock = requirement.amount * (double)Math.Max(1, requiredUses);
                var plannedUses = Math.Max(1, requiredUses - ReserveUses(kind));
                var desired = MaterialReservePolicy.RequiredStock(material, stock,
                    requirement.amount * (double)plannedUses);
                if (double.IsFinite(capacity) && capacity > 0) desired = Math.Min(desired, capacity);
                var missing = Math.Max(0, desired - material.amount);
                if (missing <= 0.0001) continue;
                if (material.locked)
                {
                    unavailableReason ??= $"Material {DisplayName(material)} is locked";
                    result.Add(new SupplyNeedEstimate(null, double.PositiveInfinity));
                    continue;
                }
                var efficiency = BestForMaterialCached(material, missing);
                var density = MapEfficiencyEstimator.EffectiveDensity(efficiency);
                if (efficiency?.Map == null || density <= 0 || !double.IsFinite(efficiency.ExpectedEligibleKills))
                {
                    unavailableReason ??= $"Material {DisplayName(material)} has no unlocked automatically killable drop enemy or map";
                    result.Add(new SupplyNeedEstimate(null, double.PositiveInfinity));
                    continue;
                }
                var expectedKills = ExpectedSupplyMaterialKills(missing, efficiency.TargetsGiant,
                    projectWhetstone: kind != SmartItem.Whetstone);
                var workSeconds = expectedKills / MapEfficiencyEstimator.EstimatedTargetKillsPerMinute(
                    density, efficiency.TargetsGiant, includeTemporaryShards: false) * 60;
                var purpose = $"Quest {plan.Name} supply: craft {kind}";
                var need = new MaterialNeed(material, efficiency.Map, desired, material.amount, 2, density,
                    expectedKills, efficiency.TargetsGiant, purpose, plan.InstanceId, questItems: new[] { kind });
                result.Add(new SupplyNeedEstimate(need, workSeconds));
            }
        }
        catch
        {
            unavailableReason = "Unable to read crafting recipe or material state";
            return new List<SupplyNeedEstimate> { new(null, double.PositiveInfinity) };
        }
        return result;
    }

    private void LogQuestSupplySkip(QuestPlan plan, SmartItem kind, string reason)
    {
        var key = $"{plan?.InstanceId ?? 0}|{kind}";
        var now = UnityEngine.Time.unscaledTime;
        if (_questSupplySkipLogUntil.TryGetValue(key, out var blockedUntil) && now < blockedUntil) return;
        // Each booster owns its own throttle. A single shared key let Alternate Staff and Shards Necklace
        // overwrite one another every tick, bypassing the intended 30-second guard and logging at 2 Hz.
        _questSupplySkipLogUntil[key] = now + 300f;
        SmartLog.Debug(SmartLogCategory.Material,
            $"Quest supply deferred: item={kind}, questInstanceId={plan?.InstanceId ?? 0}; {reason}.");
    }

    private double ExpectedSupplyMaterialKills(double materialsNeeded, bool giantDrop, bool projectWhetstone)
    {
        var inventory = PlayerInventory.instance;
        if (inventory == null) return materialsNeeded / 0.10;
        try
        {
            var dropChance = inventory.dropChance;
            if (projectWhetstone && CanProjectWhetstoneBoost()) dropChance += 65;
            return StrategyMath.ExpectedEligibleKills(materialsNeeded, dropChance,
                inventory.giantsDropChance, inventory.doubleMaterialsChance, giantDrop);
        }
        catch { return materialsNeeded / 0.10; }
    }

    private bool CanProjectWhetstoneBoost()
    {
        if (!Plugin.Config.WhetstoneDuringMaterialRecovery.Value) return false;
        var item = _catalog.Get(SmartItem.Whetstone);
        if (item == null) return false;
        try
        {
        // PlayerInventory.dropChance already includes an active bonus and must not add it again.
            if (item.IsActive() || item.currentTime > 0.05) return false;
            return item.TabVisible() && item.ExtraCondition() && item.HowManyCanCraft() > 0;
        }
        catch { return false; }
    }

    private static double EstimateSupplyRouteSeconds(List<SupplyNeedEstimate> needs, BaseMap taskMap)
    {
        var valid = needs.Where(x => x.Need?.Map != null).ToArray();
        if (valid.Length == 0) return double.PositiveInfinity;
        // Different materials on one map drop in parallel during the same run; use the slowest gap for that map instead
        // of summing every material. Different maps still add sequentially.
        var work = valid.GroupBy(x => x.Need.Map.id ?? x.Need.Map.name ??
                                      x.Need.Map.GetInstanceID().ToString())
            .Sum(group => group.Max(x => x.WorkSeconds));
        var maps = valid.Select(x => x.Need.Map)
            .GroupBy(x => x.id ?? x.name ?? x.GetInstanceID().ToString())
            .Select(x => x.First()).ToList();
        var current = MapController.instance?.selectedMap;
        var transitions = maps.Count;
        if (maps.Any(x => MapEfficiencyEstimator.SameMap(x, current))) transitions--;
        if (taskMap != null && maps.All(x => !MapEfficiencyEstimator.SameMap(x, taskMap))) transitions++;
        var firstTravel = maps.Any(x => MapEfficiencyEstimator.SameMap(x, current))
            ? 0 : PortalTimingEstimator.FirstTransitionSeconds();
        var portalCycle = PortalTimingEstimator.AdditionalTransitionSeconds();
        return work + firstTravel + Math.Max(0, transitions - (firstTravel > 0 ? 1 : 0)) * portalCycle;
    }

    private double EstimateBoostedQuestSeconds(QuestPlan plan, SmartItem kind, double directSeconds)
    {
        var travel = MapEfficiencyEstimator.EstimatedTravelSeconds(plan.TargetMap);
        var work = Math.Max(0, directSeconds - travel);
        var inventory = PlayerInventory.instance;
        var liveKill = Math.Max(1, inventory?.questProgressPerKill ?? 1);
        var liveCollectable = Math.Max(1, inventory?.collectablesQuestsProgress ?? 1);
        var liveBox = Math.Max(1, inventory?.randomBoxQuestsProgress ?? 1);
        var remaining = Math.Max(0, plan.Goal - plan.Current);
        var ratio = kind switch
        {
            SmartItem.CriticalSword => CraftableYieldModel.CriticalQuestSpeedRatio(_catalog.Get(kind)),
            SmartItem.DeathwaveScepter or SmartItem.BidimensionalStaff or SmartItem.AlternateDimensionStaff =>
                liveKill / (liveKill + CraftableEffectModel.KillProgressBonus(kind)),
            SmartItem.ShardsNecklace => GiantSpawnBoostRatio(),
            SmartItem.NecklaceOfCollectables => liveCollectable /
                (liveCollectable + CraftableEffectModel.CollectablesQuestProgressBonus),
            SmartItem.RandomBoxStaff => liveBox /
                (liveBox + CraftableEffectModel.RandomBoxQuestProgressBonus),
            SmartItem.RandomStone => 1 / CraftableEffectModel.RandomStoneProgressMultiplier,
            SmartItem.Whetstone => MaterialBoostRatio(inventory),
            SmartItem.RagePill => 1 / CraftableEffectModel.RagePillPlanningMultiplier,
            _ => 1
        };
        // These two items bound a long random wait and cannot use an ordinary duration multiplier.
        if (kind == SmartItem.Specialization)
            return travel + Math.Min(work, 79.0 + remaining * 30.0);
        if (kind == SmartItem.KeyManifest)
            return travel + Math.Min(work, 60.0 + remaining * 5.0);
        return travel + work * Math.Clamp(ratio, 0, 1);
    }

    private static double GiantSpawnBoostRatio()
    {
        var current = MapEfficiencyEstimator.CurrentGiantSpawnFactor(includeTemporaryShards: true);
        // The temporary necklace adds +300 percentage points. Permanent bonuses remain in both sides.
        var boosted = StrategyBehaviour.SmartItemActive(SmartItem.ShardsNecklace)
            ? current
            : current + 3.0;
        var baseline = StrategyBehaviour.SmartItemActive(SmartItem.ShardsNecklace)
            ? MapEfficiencyEstimator.CurrentGiantSpawnFactor(includeTemporaryShards: false)
            : current;
        return Math.Clamp(baseline / Math.Max(baseline, boosted), 0, 1);
    }

    private static double MaterialBoostRatio(PlayerInventory inventory)
    {
        if (inventory == null) return 1;
        try
        {
            var before = StrategyMath.PercentageToProbability(inventory.dropChance);
            var after = StrategyMath.PercentageToProbability(inventory.dropChance + 65);
            return after <= 0 ? 1 : Math.Clamp(before / after, 0, 1);
        }
        catch { return 1; }
    }

    private static IEnumerable<SmartItem> RelevantQuestBoosters(QuestPlan plan) =>
        QuestEffectPolicy.ApplicableItems(plan?.Quest);

    private static int ReserveUses(SmartItem kind) => kind switch
    {
        SmartItem.RagePill => Math.Max(0, Plugin.Config.RagePillReserveUses.Value),
        SmartItem.Whetstone => Math.Max(0, Plugin.Config.WhetstoneReserveUses.Value),
        SmartItem.ShardsNecklace => Math.Max(0, Plugin.Config.ShardsNecklaceReserveUses.Value),
        SmartItem.CriticalSword => Math.Max(0, Plugin.Config.CriticalSwordReserveUses.Value),
        _ => Math.Max(0, Plugin.Config.QuestBoosterReserveUses.Value)
    };

    private static string DisplayName(Drop material) => string.IsNullOrWhiteSpace(material?.localizedName)
        ? material?.name ?? "Unknown Material" : material.localizedName;

    private static string DisplayCraftableName(TemporaryCraftableItem item, SmartItem fallback) =>
        string.IsNullOrWhiteSpace(item?.localizedName) ? item?.name ?? fallback.ToString() : item.localizedName;

    private static string FormatDuration(double seconds)
    {
        if (!double.IsFinite(seconds)) return "unreachable";
        if (seconds < 60) return $"{seconds:0}s";
        return $"{seconds / 60:0.0}m";
    }

    private sealed class SupplyNeedEstimate
    {
        internal MaterialNeed Need { get; }
        internal double WorkSeconds { get; }

        internal SupplyNeedEstimate(MaterialNeed need, double workSeconds)
        {
            Need = need;
            WorkSeconds = workSeconds;
        }
    }

    private sealed class KillStaffCandidate
    {
        internal SmartItem Kind { get; }
        internal int Bonus { get; }
        internal TemporaryCraftableItem Item { get; }
        internal bool Active { get; }
        internal double CurrentCoverage { get; }
        internal double Duration { get; }
        internal int ReserveUses { get; }

        internal KillStaffCandidate(SmartItem kind, int bonus, TemporaryCraftableItem item, bool active,
            double currentCoverage, double duration, int reserveUses)
        {
            Kind = kind;
            Bonus = bonus;
            Item = item;
            Active = active;
            CurrentCoverage = currentCoverage;
            Duration = duration;
            ReserveUses = reserveUses;
        }
    }

    private sealed class KillStaffStockPlan
    {
        internal KillStaffCandidate Candidate { get; }
        internal int PlannedUses { get; }
        internal int TargetStockUses { get; }
        internal double TargetCoverage { get; }
        internal bool CapacityLimited { get; }

        internal KillStaffStockPlan(KillStaffCandidate candidate, int plannedUses, int targetStockUses,
            double targetCoverage, bool capacityLimited)
        {
            Candidate = candidate;
            PlannedUses = plannedUses;
            TargetStockUses = targetStockUses;
            TargetCoverage = targetCoverage;
            CapacityLimited = capacityLimited;
        }
    }

    private sealed class KillStaffBatchOption
    {
        internal List<KillStaffStockPlan> StockPlans { get; }
        internal List<SupplyNeedEstimate> Needs { get; }
        internal double FarmSeconds { get; }
        internal double BoostedSeconds { get; }
        internal double DirectSeconds { get; }
        internal double NetSaving { get; }
        internal double ActionsBefore { get; }
        internal double ActionsAfter { get; }
        internal bool IncludesRagePill { get; }

        internal KillStaffBatchOption(List<KillStaffStockPlan> stockPlans, List<SupplyNeedEstimate> needs,
            double farmSeconds, double boostedSeconds, double directSeconds, double netSaving,
            double actionsBefore, double actionsAfter, bool includesRagePill)
        {
            StockPlans = stockPlans;
            Needs = needs;
            FarmSeconds = farmSeconds;
            BoostedSeconds = boostedSeconds;
            DirectSeconds = directSeconds;
            NetSaving = netSaving;
            ActionsBefore = actionsBefore;
            ActionsAfter = actionsAfter;
            IncludesRagePill = includesRagePill;
        }
    }

    private sealed class CombinedStaffMaterial
    {
        internal Drop Material { get; }
        internal HashSet<SmartItem> Items { get; } = new();
        internal double Desired { get; set; }
        internal double PlannedConsumption { get; set; }

        internal CombinedStaffMaterial(Drop material) => Material = material;
    }

    private sealed class QuestSupplyOption
    {
        internal SmartItem Kind { get; }
        internal MaterialNeed Need { get; }
        internal double FarmSeconds { get; }
        internal double BoostedSeconds { get; }
        internal double DirectSeconds { get; }
        internal double NetSaving { get; }
        internal int PlannedUses { get; }
        internal int StockedUses { get; }
        internal double Duration { get; }
        internal bool CapacityLimited { get; }

        internal QuestSupplyOption(SmartItem kind, MaterialNeed need, double farmSeconds, double boostedSeconds,
            double directSeconds, double netSaving, int plannedUses, int stockedUses, double duration,
            bool capacityLimited)
        {
            Kind = kind;
            Need = need;
            FarmSeconds = farmSeconds;
            BoostedSeconds = boostedSeconds;
            DirectSeconds = directSeconds;
            NetSaving = netSaving;
            PlannedUses = plannedUses;
            StockedUses = stockedUses;
            Duration = duration;
            CapacityLimited = capacityLimited;
        }
    }

    internal bool CurrentMapDrops(Drop material)
    {
        var current = MapController.instance?.selectedMap;
        return current != null && MapDrops(current, material, includeCurrentStage: true);
    }

    private MapEfficiency BestForMaterialCached(Drop material, double materialsNeeded)
    {
        if (material == null) return null;
        int id;
        try { id = material.GetInstanceID(); }
        catch { return MapEfficiencyEstimator.BestForMaterial(material, materialsNeeded); }

        if (!_materialEfficiencyCache.TryGetValue(id, out var profiles))
        {
            profiles = MapEfficiencyEstimator.MaterialProfiles(material);
            _materialEfficiencyCache[id] = profiles;
        }
        return MapEfficiencyEstimator.BestForMaterial(profiles, materialsNeeded);
    }

    private static MaterialNeed BetterNeed(MaterialNeed current, MaterialNeed candidate, BaseMap selectedMap)
    {
        if (candidate == null) return current;
        if (current == null) return candidate;
        if (candidate.Priority != current.Priority)
            return candidate.Priority < current.Priority ? candidate : current;
        var candidateCurrent = MapEfficiencyEstimator.SameMap(candidate.Map, selectedMap);
        var currentCurrent = MapEfficiencyEstimator.SameMap(current.Map, selectedMap);
        if (candidateCurrent != currentCurrent) return candidateCurrent ? candidate : current;
        return candidate.Severity > current.Severity ? candidate : current;
    }

    private void AddRequirements(Dictionary<string, MaterialTotal> totals, SmartItem kind, int targetUses, int priority)
    {
        if (targetUses <= 0) return;
        var item = _catalog.Get(kind);
        if (item == null) return;
        try
        {
            if (!item.TabVisible()) return;
            var requirements = item.GetRequirements();
            if (requirements == null) return;
            for (var i = 0; i < requirements.Length; i++)
            {
                var requirement = requirements[i];
                var material = requirement?.material;
                if (material == null || requirement.divinityPoints || requirement.amount <= 0) continue;
                var key = CraftableCatalog.Normalize(material.name);
                if (string.IsNullOrEmpty(key)) key = CraftableCatalog.Normalize(material.localizedName);
                if (string.IsNullOrEmpty(key)) continue;
                if (!totals.TryGetValue(key, out var total))
                {
                    total = new MaterialTotal(material, priority, SafetyPurpose(kind));
                    totals[key] = total;
                }
                total.Desired += requirement.amount * Math.Max(0, targetUses);
                if (priority < total.Priority)
                {
                    total.Priority = priority;
                    total.Purpose = SafetyPurpose(kind);
                }
                if (kind == SmartItem.Whetstone)
                {
                    total.Critical = true;
                    total.ExactRefillStartAmount = Math.Max(total.ExactRefillStartAmount,
                        requirement.amount * Math.Max(0, Plugin.Config.WhetstoneRefillTriggerUses.Value));
                }
                else if (kind == SmartItem.RagePill)
                {
                    var triggerUses = Math.Max(0, Plugin.Config.RagePillRefillTriggerUses.Value);
        // Zero uses means less than one recipe remains, not that a material must reach absolute zero. Subtract a small
        // amount so exactly one available recipe still does not start supply.
                    var triggerAmount = triggerUses > 0
                        ? requirement.amount * triggerUses
                        : Math.Max(0, requirement.amount - 0.0001);
                    total.ExactRefillStartAmount = Math.Max(total.ExactRefillStartAmount, triggerAmount);
                }
            }
        }
        catch (Exception ex)
        {
            SmartLog.Debug(SmartLogCategory.Material,
                $"Unable to read the {kind} recipe: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string SafetyPurpose(SmartItem kind) => kind switch
    {
        SmartItem.Whetstone => "Whetstone supply-chain safety stock",
        SmartItem.RagePill => "Rage Pill safety stock",
        _ => "Short-value-window safety stock"
    };

    private static bool MapDrops(BaseMap map, Drop target, bool includeCurrentStage)
    {
        var targetName = CraftableCatalog.Normalize(target.name);
        if (string.IsNullOrEmpty(targetName)) targetName = CraftableCatalog.Normalize(target.localizedName);
        if (string.IsNullOrEmpty(targetName)) return false;

        if (includeCurrentStage && map.currentStageEnemies != null)
        {
            for (var i = 0; i < map.currentStageEnemies.Count; i++)
                if (EnemyDrops(map.currentStageEnemies[i], targetName)) return true;
        }

        var enemies = map.enemies;
        if (enemies == null) return false;
        for (var i = 0; i < enemies.Length; i++)
            if (EnemyDrops(enemies[i], targetName)) return true;
        return false;
    }

    private static bool EnemyDrops(Enemy enemy, string targetName)
    {
        enemy = MapEfficiencyEstimator.CurrentStage(enemy);
        if (MapEfficiencyEstimator.IsEnemyUnlocked(enemy))
        {
            var drop = enemy.drop;
            if (drop != null)
            {
                var name = CraftableCatalog.Normalize(drop.name);
                if (string.IsNullOrEmpty(name)) name = CraftableCatalog.Normalize(drop.localizedName);
                if (name == targetName) return true;
            }
        }
        return false;
    }

    private static bool SameMaterial(Drop left, Drop right)
    {
        if (left == null || right == null) return false;
        var leftName = CraftableCatalog.Normalize(left.name);
        if (string.IsNullOrEmpty(leftName)) leftName = CraftableCatalog.Normalize(left.localizedName);
        var rightName = CraftableCatalog.Normalize(right.name);
        if (string.IsNullOrEmpty(rightName)) rightName = CraftableCatalog.Normalize(right.localizedName);
        return left == right || (!string.IsNullOrEmpty(leftName) && leftName == rightName);
    }

    private sealed class MaterialTotal
    {
        internal readonly Drop Material;
        internal double Desired;
        internal int Priority;
        internal bool Critical;
        internal double ExactRefillStartAmount = -1;
        internal string Purpose;

        internal MaterialTotal(Drop material, int priority, string purpose)
        {
            Material = material;
            Priority = priority;
            Purpose = purpose;
        }
    }
}

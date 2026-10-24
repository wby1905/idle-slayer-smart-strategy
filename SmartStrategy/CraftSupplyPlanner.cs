using Il2Cpp;

namespace SmartStrategy;

internal sealed class CraftSupplyQuote
{
    internal bool Reachable => string.IsNullOrEmpty(BlockedReason) && double.IsFinite(ReadySeconds);
    internal string BlockedReason { get; set; }
    internal double ReadySeconds { get; set; }
    internal double FarmingSeconds { get; set; }
    internal double RefineryWorkSeconds { get; set; }
    internal bool UsesRefinery { get; set; }
    internal List<MaterialNeed> RawNeeds { get; } = new();
    internal List<RefineryForecastDemand> RefineryDemands { get; } = new();
}

// One recipe expansion for quest and opportunity supply: inventory -> ordinary inputs and
// refined outputs -> raw inputs -> acquisition route -> the same batch scheduler used to execute.
// Raw acquisition is conservative: newly farmed inputs become available at the end of the route.
// Queues already running continue in parallel. There is no invented instantaneous refining step.
internal sealed class CraftSupplyPlanner
{
    private readonly Dictionary<int, IReadOnlyList<MapEfficiency>> _profiles = new();
    private float _profilesExpireAt;

    internal static bool HasRefinedIngredients(TemporaryCraftableItem item)
    {
        var requirements = item?.GetRequirements();
        var sources = RefineryManager.instance?.GetRefinableDrops();
        if (requirements == null || sources == null) return false;
        foreach (var requirement in requirements)
            if (requirement?.material != null && !requirement.divinityPoints)
                foreach (var raw in sources)
                    if (RefinedCraftingSupply.Same(raw?.refinedTarget, requirement.material)) return true;
        return false;
    }

    internal CraftSupplyQuote Quote(TemporaryCraftableItem item, int copies, BaseMap returnMap,
        int priority, int questId, Func<Drop, double> protectedRaw, SmartItem? taskItem = null)
    {
        var quote = new CraftSupplyQuote();
        try
        {
            if (!CraftableCatalog.HasUnlockedRecipe(item) || copies <= 0) return Block(quote, "Recipe is locked");
            if (UnityEngine.Time.unscaledTime >= _profilesExpireAt)
            { _profiles.Clear(); _profilesExpireAt = UnityEngine.Time.unscaledTime + 30; }
            var manager = RefineryManager.instance;
            var refinable = new Dictionary<string, Drop>(StringComparer.Ordinal);
            if (manager?.GetRefinableDrops() is { } sources)
                foreach (var raw in sources)
                    if (raw?.refinedTarget != null) refinable[Key(raw.refinedTarget)] = raw;
            var slots = new List<RefineryForecastSlot>();
            var committed = new Dictionary<string, double>(StringComparer.Ordinal);
            var inventoryNeeds = new Dictionary<string, Input>();
            var requirements = item.GetRequirements();
            if (requirements == null) return Block(quote, "Recipe requirements are unavailable");
            var ingredients = new Dictionary<string, (Drop Material, double Amount)>(StringComparer.Ordinal);
            foreach (var requirement in requirements)
            {
                if (requirement == null || requirement.amount <= 0) continue;
                if (requirement.divinityPoints || requirement.material == null || !double.IsFinite(requirement.amount))
                    return Block(quote, "Recipe requires an unsupported or invalid resource");
                var key = Key(requirement.material);
                var previous = ingredients.TryGetValue(key, out var existing) ? existing.Amount : 0;
                ingredients[key] = (requirement.material, previous + requirement.amount);
            }
            var refinedInputs = new List<(Drop Output, Drop Raw, int Missing, int NewOutputs)>();
            foreach (var ingredient in ingredients.Values)
            {
                var output = ingredient.Material;
                var wanted = ingredient.Amount * copies + MaterialReservePolicy.MinimumReserve(output);
                if (!FiniteStock(output) || !double.IsFinite(wanted) || wanted > output.GetMaxAmount() + 0.0001)
                    return Block(quote, "Recipe stock target exceeds material capacity");
                var missing = Math.Max(0, wanted - output.amount);
                if (missing <= 0.0001) continue;
                if (!refinable.TryGetValue(Key(output), out var raw))
                { AddInput(output, wanted, false); continue; }
                quote.UsesRefinery = true;
                if (refinedInputs.Count == 0)
                {
                    if (!RefinedCraftingSupply.FeatureUnlocked() || manager?.slots == null)
                        return Block(quote, "Refinery building/Book of Refinement is locked");
                    if (manager.unlockedSlots <= 0 || !double.IsFinite(RefineryManager.SECONDS_PER_CYCLE) ||
                        RefineryManager.SECONDS_PER_CYCLE <= 0 || RefineryManager.MATERIALS_PER_OUTPUT <= 0)
                        return Block(quote, "Invalid native refinery parameters");
                    for (var i = 0; i < manager.slots.Length; i++)
                    {
                        var slot = manager.slots[i];
                        if (slot?.material == null) continue;
                        var produced = slot.material.refinedTarget;
                        var pending = slot.TotalOutputs - slot.amountClaimed;
                        var ready = slot.GetClaimableOutputs();
                        if (produced == null || !double.IsFinite(pending) || pending < 0 || pending > int.MaxValue ||
                            Math.Abs(pending - Math.Round(pending)) > 0.0001 ||
                            !double.IsFinite(ready) || ready < 0 || ready > pending || Math.Abs(ready - Math.Round(ready)) > 0.0001 ||
                            !double.IsFinite(slot.remainingTime) || slot.remainingTime < 0)
                            return Block(quote, "Invalid existing refinery queue");
                        var key = Key(produced);
                        committed[key] = (committed.TryGetValue(key, out var value) ? value : 0) + pending;
                        if (!FiniteStock(produced) || committed[key] + produced.amount > produced.GetMaxAmount() + 0.0001)
                            return Block(quote, "Existing refinery output is blocked by bag capacity");
                        slots.Add(new RefineryForecastSlot(i, key, slot.remainingTime,
                            (int)Math.Round(pending - ready), (int)Math.Round(ready)));
                    }
                }
                var needed = (int)Math.Clamp(Math.Ceiling(missing - 0.0001), 0, int.MaxValue);
                var pendingCount = committed.TryGetValue(Key(output), out var pendingOutput) ? pendingOutput : 0;
                var fresh = (int)Math.Max(0, needed - pendingCount);
                refinedInputs.Add((output, raw, needed, fresh));
                if (fresh > 0)
                {
                    var reserve = protectedRaw(raw);
                    if (!double.IsFinite(reserve) || reserve < 0 || reserve + RefineryManager.MATERIALS_PER_OUTPUT > raw.GetMaxAmount() + 0.0001)
                        return Block(quote, "Raw capacity cannot fit one refinery cycle above protected stock");
                    AddInput(raw, reserve, true, fresh * RefineryManager.MATERIALS_PER_OUTPUT);
                    quote.RefineryWorkSeconds += fresh * RefineryManager.SECONDS_PER_CYCLE;
                }
            }

            var work = new List<(MaterialNeed Need, double Seconds)>();
            foreach (var input in inventoryNeeds.Values)
            {
                var raw = input.Material;
                if (input.ForRefinery && input.Floor + RefineryManager.MATERIALS_PER_OUTPUT > raw.GetMaxAmount() + 0.0001)
                    return Block(quote, "Direct/protected raw stock leaves no room for a refining cycle");
                var gap = Math.Max(0, input.Wanted - raw.amount);
                if (gap <= 0.0001) continue;
                if (raw.locked || !FiniteStock(raw)) return Block(quote, $"Material {raw.name} is unavailable");
                if (!_profiles.TryGetValue(raw.GetInstanceID(), out var profiles))
                    _profiles[raw.GetInstanceID()] = profiles = MapEfficiencyEstimator.MaterialProfiles(raw);
                var efficiency = MapEfficiencyEstimator.BestForMaterial(profiles, gap);
                var density = MapEfficiencyEstimator.EffectiveDensity(efficiency);
                if (efficiency?.Map == null || density <= 0 || !double.IsFinite(efficiency.ExpectedEligibleKills))
                    return Block(quote, $"Material {raw.name} has no unlocked automatic source");
                var seconds = efficiency.ExpectedEligibleKills /
                    MapEfficiencyEstimator.EstimatedTargetKillsPerMinute(density, efficiency.TargetsGiant, includeTemporaryShards: false) * 60;
                var need = new MaterialNeed(raw, efficiency.Map, Math.Min(raw.GetMaxAmount(), input.Wanted), raw.amount,
                    priority, density, efficiency.ExpectedEligibleKills, efficiency.TargetsGiant,
                    $"Complete recipe supply: {item.name}", questId, forRefinery: input.ForRefinery,
                    questItems: taskItem.HasValue ? new[] { taskItem.Value } : null);
                quote.RawNeeds.Add(need);
                work.Add((need, seconds));
            }
            quote.FarmingSeconds = RouteSeconds(work, returnMap);
            foreach (var refined in refinedInputs)
                quote.RefineryDemands.Add(new RefineryForecastDemand(Key(refined.Output), refined.Missing,
                    priority, refined.NewOutputs, refined.NewOutputs > 0 ? quote.FarmingSeconds : 0));
            var refinerySeconds = quote.RefineryDemands.Count == 0 ? 0 : RefineryForecast.Plan(slots,
                quote.RefineryDemands, manager.unlockedSlots, RefineryManager.SECONDS_PER_CYCLE,
                Math.Clamp(Plugin.Config.RefineryMaximumBatchOutputs.Value, 1, 100)).CompletionSeconds;
            quote.ReadySeconds = Math.Max(quote.FarmingSeconds, refinerySeconds);
            if (!double.IsFinite(quote.ReadySeconds)) quote.BlockedReason = "Refinery schedule is not reachable";
            return quote;

            void AddInput(Drop material, double wanted, bool forRefinery, double feed = 0)
            {
                var key = Key(material);
                if (!inventoryNeeds.TryGetValue(key, out var input))
                    inventoryNeeds[key] = input = new Input(material);
                // Preserve the larger direct/protected floor, then add feed. Direct ingredients
                // and feed are different units; adding the reserve twice is equally incorrect.
                input.Floor = Math.Max(input.Floor, wanted);
                input.Feed += feed;
                input.ForRefinery |= forRefinery;
            }
        }
        catch (Exception ex) { return Block(quote, "Recipe supply unavailable: " + ex.GetType().Name); }
    }

    private static double RouteSeconds(List<(MaterialNeed Need, double Seconds)> needs, BaseMap target)
    {
        if (needs.Count == 0) return 0;
        var groups = needs.GroupBy(n => n.Need.Map.id ?? n.Need.Map.name).ToArray();
        var work = groups.Sum(g => g.Max(n => n.Seconds));
        var current = MapController.instance?.selectedMap;
        var currentIncluded = groups.Any(g => MapEfficiencyEstimator.SameMap(g.First().Need.Map, current));
        var transitions = groups.Length - (currentIncluded ? 1 : 0);
        if (target != null && groups.All(g => !MapEfficiencyEstimator.SameMap(g.First().Need.Map, target))) transitions++;
        return work + (currentIncluded ? 0 : PortalTimingEstimator.FirstTransitionSeconds()) +
            Math.Max(0, transitions - (currentIncluded ? 0 : 1)) * PortalTimingEstimator.AdditionalTransitionSeconds();
    }
    private static bool FiniteStock(Drop d) => d != null && double.IsFinite(d.amount) && d.amount >= 0 &&
        double.IsFinite(d.GetMaxAmount()) && d.GetMaxAmount() > 0 && d.amount <= d.GetMaxAmount() + 0.0001;
    private static CraftSupplyQuote Block(CraftSupplyQuote quote, string reason)
    { quote.BlockedReason = reason; quote.ReadySeconds = double.PositiveInfinity; return quote; }
    private static string Key(Drop drop) => string.IsNullOrEmpty(drop.id) ? drop.GetInstanceID().ToString() : drop.id;
    private sealed class Input
    {
        internal Drop Material;
        internal double Floor, Feed;
        internal double Wanted => Floor + Feed;
        internal bool ForRefinery;
        internal Input(Drop material) => Material = material;
    }
}

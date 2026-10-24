using Il2Cpp;
using UnityEngine;

namespace SmartStrategy;

public sealed partial class StrategyBehaviour
{
    private void HandleRefineryAutomation(SoulsEventState souls)
    {
        if (Time.unscaledTime < _nextSupplementalRefineAt) return;
        _nextSupplementalRefineAt = Time.unscaledTime + 2f;
        try
        {
            var requests = new List<RefineryRecipeRequest>();
            if (!(Plugin.EndgameModeActive && Plugin.Config.PermanentTimedCraftableEffectsEnabled.Value))
            {
                var config = Plugin.Config;
                var soulWindow = IsQualifyingSoulEvent(souls);
                Add(SmartItem.SoulsSpyglass, config.SoulsSpyglassEnabled.Value,
                    config.SoulsSpyglassReserveUses.Value, 1, soulWindow,
                    soulWindow ? Math.Max(0, souls.SecondsLeft - 5) : null,
                    config.SoulsSpyglassRefreshMinutes.Value * 60);
                Add(SmartItem.CriticalSword, config.CriticalSwordEnabled.Value,
                    config.CriticalSwordReserveUses.Value, 0,
                    soulWindow && souls.Multiplier >= config.CriticalSwordMinimumMultiplier.Value && CurrentMapHasGiant(),
                    soulWindow ? Math.Max(0, souls.SecondsLeft - 5) : null, soulWindow ? souls.SecondsLeft + 5 : 0);
                foreach (var kind in SupplementalKinds)
                {
                    var enabled = kind == SmartItem.BunnyPaw ? config.BunnyPawEnabled.Value :
                        kind == SmartItem.DeathBringer ? config.DeathBringerEnabled.Value : config.CpsSpyglassEnabled.Value;
                    var immediate = SupplementalContext(kind, souls, out _);
                    Add(kind, enabled, 0, kind == SmartItem.BunnyPaw ? 3 : 2, immediate,
                        kind == SmartItem.CpsSpyglass && immediate ? Math.Max(0, CoinWindowSeconds() - 5) : null, 0);
                }

                void Add(SmartItem kind, bool enabled, int reserve, int priority, bool immediate,
                    double? deadline, double refreshAt)
                {
                    if (!enabled) return;
                    var item = _catalog.Get(kind);
                    if (!CraftableCatalog.HasUnlockedRecipe(item)) return;
                    var coverage = PermanentClientBonuses.CoverageSeconds(item);
                    if (!double.IsFinite(coverage)) return;
                    // Future stock is one recipe, not duration/600 repeated crafts. The item is consumed
                    // only by its existing event/combat strategy, never by the refinery itself.
                    var horizon = Math.Max(0, config.RefineryLookaheadMinutes.Value * 60);
                    if (coverage > Math.Max(horizon, refreshAt)) return;
                    var usableNow = immediate && coverage <= Math.Max(0.05, refreshAt);
                    requests.Add(new RefineryRecipeRequest(item, 1 + Math.Clamp(reserve, 0, 9),
                        usableNow ? priority : priority + 10, usableNow, usableNow ? deadline : null));
                }
            }
            // A paid burst and portal protection keep ownership of consumable budgets. Claiming is
            // harmless housekeeping, but no new commitments or premium transactions are made here.
            var allowNew = !PaidSoulsBurstOwnsControl && !PortalQuestProtectionActive() &&
                (!_materialPlanDirty || !Plugin.Config.AutomaticMaterialTravel.Value) &&
                Time.unscaledTime - _lastCraftAt >= 0.45f;
            if (_supplementalRefining.Tick(requests, ProtectedStockForRefinery, allowNew)) InvalidateMaterialPlan();
        }
        catch (Exception ex)
        {
            _supplementalRefining.Invalidate();
            _nextSupplementalRefineAt = Time.unscaledTime + 30;
            SmartLog.Warning(SmartLogCategory.Material,
                $"Refinery planning deferred; other strategies continue: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private double ProtectedStockForRefinery(Drop raw)
    {
        var reserve = MaterialReservePolicy.MinimumReserve(raw);
        // Do not consume stock currently being prepared by the ordinary/paid-burst planner.
        if (_materialNeed is { ForRefinery: false } && RefinedCraftingSupply.Same(_materialNeed.Material, raw))
            reserve = Math.Max(reserve, _materialNeed.Desired);
        if (_questPlan?.Quest != null && _questPlan.Current < _questPlan.Goal)
        {
            var planIds = (_parallelQuestPlans ?? Array.Empty<QuestPlan>()).Append(_questPlan)
                .Where(p => p?.Quest != null && p.Current < p.Goal && QuestApi.CanProgressNow(p.Quest))
                .Select(p => p.InstanceId);
            var staffStock = _materials?.PlannedRawReserve(raw, planIds) ?? 0;
            reserve = Math.Max(reserve, staffStock + MaterialReservePolicy.MinimumReserve(raw));
        }
        if (!(Plugin.EndgameModeActive && Plugin.Config.PermanentTimedCraftableEffectsEnabled.Value))
            reserve = Math.Max(reserve, MaterialReservePolicy.MinimumReserve(raw) +
                RefinedCraftingSupply.RecipeAmount(_catalog.Get(SmartItem.RagePill), raw) *
                Math.Clamp(Plugin.Config.MaximumRagePillsPerEvent.Value, 1, 2));
        return reserve;
    }

    private MaterialNeed IncludeRefinerySupply(MaterialNeed ordinary)
    {
        // Critical supply and profitable quest-item preparations keep their existing precedence.
        if (ordinary != null && ordinary.Priority < 4) return ordinary;
        var refinery = _supplementalRefining.FindRawMaterialNeed(_questPlan,
            PortalQuestProtectionActive() || PaidSoulsBurstOwnsControl || _questPlan?.StrategicUnlockBonus > 0);
        return refinery ?? ordinary;
    }

    private static double CoinWindowSeconds()
    {
        static double Seconds(RandomEvent effect) => effect != null && effect.IsActive() &&
            double.IsFinite(effect.timeLeft) ? Math.Max(0, effect.timeLeft) : 0;
        var manager = RandomEventManager.instance;
        if (manager == null) return 0;
        var seconds = Math.Max(Seconds(manager.cpsBonus9x), Math.Max(Seconds(manager.dailyCpSBonus),
            Math.Max(Seconds(manager.frenzy), Seconds(JewelsOfSoulManager.instance?.paidCpSBonus))));
        if (manager.randomEvents != null)
            foreach (var effect in manager.randomEvents)
                if (effect != null && (effect.TryCast<CpSBonus>() != null || effect.TryCast<CoinValueBonus>() != null ||
                                      effect.TryCast<EquipmentBonus>() != null)) seconds = Math.Max(seconds, Seconds(effect));
        return seconds;
    }
}

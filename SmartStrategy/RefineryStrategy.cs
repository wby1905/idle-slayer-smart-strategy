using Il2Cpp;
using UnityEngine;

namespace SmartStrategy;

public sealed partial class StrategyBehaviour
{
    private readonly CraftingDemandPolicy _craftingDemand = new();

    private void HandleRefineryAutomation(SoulsEventState souls)
    {
        if (Time.unscaledTime < _nextSupplementalRefineAt) return;
        _nextSupplementalRefineAt = Time.unscaledTime + 2f;
        try
        {
            var requests = new List<RefineryRecipeRequest>();
            var exclusivePhase = PaidSoulsBurstOwnsControl || PortalQuestProtectionActive();
            if (!exclusivePhase && !(Plugin.EndgameModeActive && Plugin.Config.PermanentTimedCraftableEffectsEnabled.Value))
            {
                var config = Plugin.Config;
                var soulWindow = IsQualifyingSoulEvent(souls);
                _craftingDemand.Observe(SmartItem.SoulsSpyglass, soulWindow);
                _craftingDemand.Observe(SmartItem.CriticalSword, soulWindow && CurrentMapHasGiant());
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
                    _craftingDemand.Observe(kind, kind == SmartItem.DeathBringer
                        ? immediate && GameState.current == GameStates.BattleMode : immediate);
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
                    var usableNow = immediate && coverage <= Math.Max(0.05, refreshAt);
                    var decision = _craftingDemand.Decide(kind, item, reserve, usableNow, deadline, coverage,
                        copies => _supplementalRefining.SupplyPlanner.Quote(item, copies, _questPlan?.TargetMap,
                            4, 0, ProtectedStockForRefinery));
                    if (decision.Copies <= 0) return;
                    requests.Add(new RefineryRecipeRequest(item, decision.Copies,
                        decision.Farm ? usableNow ? priority : priority + 10 : priority + RefineryRecipeRequest.SurplusPriority,
                        usableNow, usableNow ? deadline : null, decision.Farm,
                        Purpose: decision.Reason, AllowPremium: decision.Premium));
                }
                requests.AddRange(_materials.RefinedQuestRequests(
                    (_parallelQuestPlans ?? Array.Empty<QuestPlan>()).Append(_questPlan)));
            }
            // Exclusive phases publish no recipe/premium requests. Their Pill and active supply
            // reservations are protected below, so other surplus raw inputs can still use free slots.
            var allowNew = (!_materialPlanDirty || !Plugin.Config.AutomaticMaterialTravel.Value || exclusivePhase) &&
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
        if (_materialNeed is { ForRefinery: false } && QuestSupplyStillApplicable(_materialNeed) &&
            (_materialNeed.Priority < 4 || _materialNeed.QuestPlanId != 0 && !_materialNeed.Opportunistic) &&
            RefinedCraftingSupply.Same(_materialNeed.Material, raw))
            reserve = Math.Max(reserve, _materialNeed.Desired);
        if (PaidSoulsBurstOwnsControl)
        {
            if (_paidSupplyNeed?.Material != null && RefinedCraftingSupply.Same(_paidSupplyNeed.Material, raw))
                reserve = Math.Max(reserve, _paidSupplyNeed.Desired);
            var perPill = RefinedCraftingSupply.RecipeAmount(_catalog.Get(SmartItem.RagePill), raw);
            if (perPill > 0)
            {
                var uses = Math.Max(0, _paidTargetRagePillUses);
                if (_paidSoulsPhase is PaidSoulsBurstPhase.PurchaseRequested or PaidSoulsBurstPhase.ChestHunt or PaidSoulsBurstPhase.Bursting)
                {
                    var remaining = _paidSoulsEffect?.timeLeft ?? double.NaN;
                    var duration = RageModeManager.instance?.GetDuration() ?? double.NaN;
                    // Unknown effect timing cannot release pre-reserved Pill inputs to background refining.
                    if (!double.IsFinite(remaining) || remaining < 0 || !double.IsFinite(duration) || duration <= 0)
                        return Math.Max(reserve, raw.amount);
                    uses = (int)Math.Clamp(Math.Ceiling(remaining / duration) + 1, 1, int.MaxValue);
                }
                reserve = Math.Max(reserve, MaterialReservePolicy.MinimumReserve(raw) + perPill * uses);
            }
        }
        if (_questPlan?.Quest != null && _questPlan.Current < _questPlan.Goal)
        {
            var plans = (_parallelQuestPlans ?? Array.Empty<QuestPlan>()).Append(_questPlan)
                .Where(p => p?.Quest != null && p.Current < p.Goal && QuestApi.CanProgressNow(p.Quest));
            var staffStock = _materials?.PlannedRawReserve(raw, plans) ?? 0;
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

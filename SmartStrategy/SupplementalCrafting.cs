using Il2Cpp;
using UnityEngine;

namespace SmartStrategy;

public sealed partial class StrategyBehaviour
{
    private readonly RefinedCraftingSupply _supplementalRefining = new();
    private static readonly SmartItem[] SupplementalKinds =
        { SmartItem.DeathBringer, SmartItem.CpsSpyglass, SmartItem.BunnyPaw };
    private float _nextSupplementalCraftCheckAt;
    private float _nextSupplementalRefineAt;
    private float _nextSupplementalCraftErrorLogAt;

    private bool HandleSupplementalCraftables(SoulsEventState souls)
    {
        // Endgame/AFK owns all unlocked timed effects independently of these normal-mode switches.
        // It never needs to spend materials or schedule refining for permanent effects.
        if (Plugin.EndgameModeActive && Plugin.Config.PermanentTimedCraftableEffectsEnabled.Value ||
            PaidSoulsBurstOwnsControl || PortalQuestProtectionActive() ||
            Time.unscaledTime < _nextSupplementalCraftCheckAt) return false;
        _nextSupplementalCraftCheckAt = Time.unscaledTime + 1f;
        if (!QuestDirector.IsStandardAvailableMap(MapController.instance?.selectedMap)) return false;

        try
        {
            var candidates = new List<(SmartItem Kind, TemporaryCraftableItem Item, string Reason)>(3);
            foreach (var kind in SupplementalKinds)
            {
                if (!SupplementalContext(kind, souls, out var reason)) continue;
                var item = _catalog.Get(kind);
                if (!CraftableCatalog.HasUnlockedRecipe(item) ||
                    PermanentClientBonuses.CoverageSeconds(item) > 0.05 || item.IsActive()) continue;
                if (!double.IsFinite(item.GetDuration()) || item.GetDuration() <= 0) continue;
                candidates.Add((kind, item, reason));
            }

            // Existing stock is consumed only once per effect, through the common native crafting
            // path and rare-material reserves. Do not top up a 12-hour buff while it is still active.
            foreach (var candidate in candidates)
            {
                var crafts = candidate.Item.HowManyCanCraft();
                if (!double.IsFinite(crafts) || crafts < 1 ||
                    !CanCraftWithoutReducingPaidPillStock(candidate.Kind)) continue;
                if (TryCraft(candidate.Kind, 0, candidate.Reason)) return true;
            }

            return false; // Refinery housekeeping and advance supply have their own global cadence.
        }
        catch (Exception ex)
        {
            _nextSupplementalCraftCheckAt = Time.unscaledTime + 10f;
            if (Time.unscaledTime >= _nextSupplementalCraftErrorLogAt)
            {
                _nextSupplementalCraftErrorLogAt = Time.unscaledTime + 30f;
                SmartLog.Warning(SmartLogCategory.Material,
                    $"Supplemental craftables are temporarily unavailable; other strategies continue: {ex.GetType().Name}: {ex.Message}");
            }
            return false;
        }
    }

    private bool SupplementalContext(SmartItem kind, SoulsEventState souls, out string reason)
    {
        reason = string.Empty;
        switch (kind)
        {
            case SmartItem.DeathBringer:
                if (!Plugin.Config.DeathBringerEnabled.Value || !Plugin.Config.GiantRapidAttackEnabled.Value ||
                    NativeRageOwnsMovement() || !CurrentMapHasGiant()) return false;
                reason = "Ordinary Giant combat on the current map";
                return true;
            case SmartItem.CpsSpyglass:
                if (!Plugin.Config.CpsSpyglassEnabled.Value || !HasLiveCoinMultiplier()) return false;
                reason = "Live CPS or coin-value multiplier window";
                return true;
            case SmartItem.BunnyPaw:
                if (!Plugin.Config.BunnyPawEnabled.Value || Upgrades.list?.RandomBox?.bought != true ||
                    RandomEventManager.instance == null || souls.Active || HasTrackedCoinBoxQuest()) return false;
                reason = "Searching Random Boxes for Souls events";
                return true;
            default:
                return false;
        }
    }

    private bool HasTrackedCoinBoxQuest()
    {
        static bool Matches(QuestPlan plan) => plan?.Quest != null &&
            plan.Type == QuestKind.GetCoinsFromRandomBox && plan.Current < plan.Goal &&
            QuestApi.CanProgressNow(plan.Quest);
        return Matches(_questPlan) || _parallelQuestPlans?.Any(Matches) == true;
    }

    private static bool HasLiveCoinMultiplier()
    {
        static bool Live(RandomEvent effect) => effect != null && effect.IsActive() && effect.timeLeft > 5;
        var manager = RandomEventManager.instance;
        if (manager == null) return false;
        if (Live(manager.cpsBonus9x) || Live(manager.dailyCpSBonus) || Live(manager.frenzy) ||
            Live(JewelsOfSoulManager.instance?.paidCpSBonus)) return true;
        if (manager.randomEvents == null) return false;
        foreach (var effect in manager.randomEvents)
            if (effect != null && (effect.TryCast<CpSBonus>() != null || effect.TryCast<CoinValueBonus>() != null ||
                                  effect.TryCast<EquipmentBonus>() != null) && Live(effect)) return true;
        return false;
    }
}

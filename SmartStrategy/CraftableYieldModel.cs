using Il2Cpp;

namespace SmartStrategy;

/// <summary>
/// Marginal effect contributions, not whole-run yield promises. The caller must weight these
/// by observed eligible opportunities and remaining useful duration before buying or supplying.
/// Native evidence: Idle Slayer 7.3.6, Steam build 26139124.
/// CalculateValues adds each active item's newBenefit (0x1804F861D); AddBenefit applies the
/// verified benefit identities. Utils.GetMultiplicativeValue (0x18040CB20) multiplies each
/// percentage modifier independently, while critical chance is an additive percentage point value.
/// </summary>
internal static class CraftableYieldModel
{
    private const double Epsilon = 0.000001;

    // Returns boosted/unboosted completion time for a critical-count quest. Removal returns
    // unboosted/current time and is meaningful only while this exact item is active. Unknown
    // state or a zero baseline rate returns neutral 1 instead of manufacturing infinite savings.
    internal static double CriticalQuestSpeedRatio(TemporaryCraftableItem item, bool removeActive = false)
    {
        try
        {
            if (!TryCriticalState(item, out var baseline, out var boosted, out var active, out _)) return 1;
            if (removeActive && !active) return 1;
            if (baseline <= Epsilon || boosted <= Epsilon) return 1;
            var ratio = removeActive ? boosted / baseline : baseline / boosted;
            return double.IsFinite(ratio) && ratio > 0 ? ratio : 1;
        }
        catch { return 1; }
    }

    // gainFraction is the isolated contribution of this item relative to a baseline without it.
    // An active item is removed before reconstructing that baseline; this does not mean crafting
    // another copy while active increases potency. Renewal timing belongs to the decision layer.
    internal static bool TryMarginalGain(SmartItem kind, TemporaryCraftableItem item,
        out double gainFraction, out string reason)
    {
        gainFraction = 0;
        reason = "The native marginal effect is not established for this item.";
        try
        {
            var inventory = PlayerInventory.instance;
            var benefits = Benefits.list;
            if (inventory == null || benefits == null || item == null || inventory.ignoreTemporaryBonusesForIdleRewards)
            {
                reason = "Live native effects are not available or temporary bonuses are being excluded.";
                return false;
            }

            switch (kind)
            {
                case SmartItem.CriticalSword:
                {
                    if (!TryCriticalState(item, out var baseline, out var boosted, out _, out reason)) return false;
                    var criticalYield = inventory.criticalGatherYield;
                    if (!double.IsFinite(criticalYield) || criticalYield < 1)
                    {
                        reason = "The live critical Souls multiplier is invalid.";
                        return false;
                    }
                    // EnemySlayed 0x1804FB8A6 compares against a uniform 0..100 roll, excludes
                    // Enemy.cantBeCritical, and multiplies the reward by criticalGatherYield.
                    var without = 1 + baseline * (criticalYield - 1);
                    var with = 1 + boosted * (criticalYield - 1);
                    gainFraction = with / without - 1;
                    reason = $"Expected Souls per critical-eligible kill: chance {baseline:P1} -> {boosted:P1}, " +
                             $"live critical reward {criticalYield:0.###}x; enemy eligibility and opportunity time are separate.";
                    return KnownGain(gainFraction);
                }
                case SmartItem.DeathBringer:
                {
                    if (!TryPercentContribution(item, benefits.IncreaseSwordDamageModifier,
                            inventory.swordDamageModifier, out gainFraction, out reason)) return false;
                    if (!double.IsFinite(inventory.swordDamage) || inventory.swordDamage <= 0)
                    {
                        gainFraction = 0;
                        reason = "Live sword damage is unavailable.";
                        return false;
                    }
                    reason = $"Native sword-hit damage contribution +{gainFraction:P1}; applies during actual sword combat. " +
                             "This is a damage-rate bound, not a whole-map farming-speed gain; hit rounding and spawn time remain separate.";
                    return true;
                }
                case SmartItem.SoulsSpyglass:
                case SmartItem.SoulsCompass:
                    if (!TryPercentContribution(item, benefits.IncreaseSouls, inventory.soulsBonusList,
                            out gainFraction, out reason)) return false;
                    reason = $"Independent native Souls multiplier contribution +{gainFraction:P1}; " +
                             "only useful Souls-producing opportunity time should be counted.";
                    return true;
                case SmartItem.CpsSpyglass:
                    if (!TryPercentContribution(item, benefits.IncreaseCPS, inventory.cpsBonusList,
                            out gainFraction, out reason)) return false;
                    reason = $"Independent native CpS multiplier contribution +{gainFraction:P1}; " +
                             "other reward channels and time without a useful coin objective are excluded.";
                    return true;
                case SmartItem.BunnyPaw:
                    if (!TryReadBenefit(item, benefits.DecreaseRandomBoxCoinsChance, out var reduction, out reason))
                        return false;
                    // LuckyCoins.GetChance (0x1802FFDC0) subtracts this aggregate field from its
                    // own chance. GetRandomEvent (0x18030C420) shuffles, rolls each event in order,
                    // and takes the first success; SelectRandomEvent then rejects ineligible events.
                    // A reduction in one event's trial chance is not the same percentage of
                    // extra Souls events, nor the same percentage of total Souls earned.
                    reason = $"Native Lucky Coins trial chance reduction is {reduction:0.###} percentage points. " +
                             "Shuffle/first-success selection and eligibility retries do not establish a direct Souls-yield ratio; " +
                             "use observations or existing surplus stock, not an assumed percentage for farming or premium spending.";
                    return false;
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            gainFraction = 0;
            reason = $"Native marginal effect state is unavailable ({ex.GetType().Name}).";
            return false;
        }
    }

    private static bool TryCriticalState(TemporaryCraftableItem item, out double baseline,
        out double boosted, out bool active, out string reason)
    {
        baseline = boosted = 0;
        active = false;
        var inventory = PlayerInventory.instance;
        if (inventory == null || inventory.ignoreTemporaryBonusesForIdleRewards ||
            !TryReadBenefit(item, Benefits.list?.IncreaseCriticalGatherChance, out var points, out reason))
        {
            reason = "Native critical-chance effect or current inventory is unavailable.";
            return false;
        }
        var chance = inventory.criticalGatherChance;
        active = item.IsActive();
        if (!double.IsFinite(chance) || chance < 0 || active && chance + Epsilon < points)
        {
            reason = "Live critical chance has not reconciled with this active item.";
            return false;
        }
        // Subtract before clamping: a live 140% chance with a +45-point item has a 95%,
        // not 55%, baseline. The native roll naturally saturates at 100% even if the field does not.
        var without = Math.Max(0, chance - (active ? points : 0));
        baseline = Math.Clamp(without / 100, 0, 1);
        boosted = Math.Clamp((without + points) / 100, 0, 1);
        reason = "Native additive critical chance, capped by the 0..100 roll.";
        return true;
    }

    private static bool TryPercentContribution(TemporaryCraftableItem item, NewBenefit expected,
        Il2CppSystem.Collections.Generic.List<double> modifiers, out double gain, out string reason)
    {
        gain = 0;
        if (!TryReadBenefit(item, expected, out var percentage, out reason)) return false;
        if (modifiers == null || modifiers.Count > 4096)
        {
            reason = "The native multiplicative modifier list is unavailable.";
            return false;
        }
        var current = 1d;
        var matchingEntry = false;
        for (var i = 0; i < modifiers.Count; i++)
        {
            var entry = modifiers[i];
            var entryFactor = 1 + entry / 100;
            if (!double.IsFinite(entry) || !double.IsFinite(entryFactor) || entryFactor <= 0)
            {
                reason = "A native percentage modifier cannot be safely evaluated.";
                return false;
            }
            matchingEntry |= Math.Abs(entry - percentage) <= Epsilon;
            current *= entryFactor;
            if (!double.IsFinite(current) || current <= 0)
            {
                reason = "The native modifier product is outside the finite evaluation range.";
                return false;
            }
        }
        var active = item.IsActive();
        if (active && !matchingEntry)
        {
            reason = "The active item has not appeared in its native modifier list yet.";
            return false;
        }
        var factor = 1 + percentage / 100;
        var without = active ? current / factor : current;
        var with = without * factor;
        if (!double.IsFinite(without) || without <= 0 || !double.IsFinite(with))
        {
            reason = "The marginal modifier contribution is outside the finite evaluation range.";
            return false;
        }
        gain = with / without - 1;
        reason = "Native independent percentage multiplier, with the active item removed from the baseline.";
        return KnownGain(gain);
    }

    private static bool TryReadBenefit(TemporaryCraftableItem item, NewBenefit expected,
        out double value, out string reason)
    {
        value = 0;
        var effect = item?.newBenefit;
        if (expected == null || effect?.benefit == null || effect.benefit != expected)
        {
            reason = "The item's native benefit identity does not match the verified effect formula.";
            return false;
        }
        value = effect.value;
        if (!double.IsFinite(value) || value < 0)
        {
            reason = "The native effect value is invalid.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static bool KnownGain(double value) => double.IsFinite(value) && value >= 0;
}

namespace SmartStrategy;

internal readonly record struct PortfolioContribution(
    double Priority,
    double RemainingProgress,
    double TargetsPerPattern,
    double CreditPerTarget);

internal static class StrategyMath
{
    internal static double PercentageToProbability(double percentagePoints)
    {
        if (double.IsNaN(percentagePoints) || percentagePoints <= 0) return 0;
        return Math.Clamp(percentagePoints / 100.0, 0, 1);
    }

    internal static double ExpectedEligibleKills(double materialsNeeded, double dropChancePoints,
        double giantDropChancePoints, double doubleMaterialChancePoints, bool giantDrop)
    {
        if (materialsNeeded <= 0) return 0;
        var dropChance = PercentageToProbability(dropChancePoints + (giantDrop ? giantDropChancePoints : 0));
        if (dropChance <= 0) return double.PositiveInfinity;
        var expectedYield = dropChance * (1 + PercentageToProbability(doubleMaterialChancePoints));
        return materialsNeeded / expectedYield;
    }

    internal static double ForecastQuestCredit(int liveCredit, int inactiveCraftableBonus, bool collab)
    {
        if (collab) return 1;
        return Math.Max(1, liveCredit) + Math.Max(0, inactiveCraftableBonus);
    }

    internal static double PortfolioUtility(IEnumerable<PortfolioContribution> contributions,
        double patternsPerMinute, double horizonSeconds, double travelSeconds)
    {
        var productiveSeconds = Math.Max(0, horizonSeconds - Math.Max(0, travelSeconds));
        if (productiveSeconds <= 0 || patternsPerMinute <= 0) return 0;
        var expectedPatterns = patternsPerMinute * productiveSeconds / 60.0;
        var utility = 0.0;
        foreach (var contribution in contributions)
        {
            if (contribution.Priority <= 0 || contribution.RemainingProgress <= 0 ||
                contribution.TargetsPerPattern <= 0 || contribution.CreditPerTarget <= 0) continue;
            var expectedProgress = contribution.TargetsPerPattern * contribution.CreditPerTarget * expectedPatterns;
            utility += contribution.Priority * Math.Min(1, expectedProgress / contribution.RemainingProgress);
        }
        return utility;
    }

    internal static bool ShouldFarmForBooster(double farmSeconds, double directSeconds, double boostedSeconds)
    {
        if (!double.IsFinite(farmSeconds) || !double.IsFinite(directSeconds) || !double.IsFinite(boostedSeconds))
            return false;
        // farmSeconds is zero when current materials suffice. This is an optimal candidate, not an invalid supply route.
        // Rejecting it forces the caller to choose the first larger staff combination that actually lacks materials.
        return farmSeconds >= 0 && boostedSeconds >= 0 && farmSeconds + boostedSeconds < directSeconds;
    }

    internal static int KillStaffMinimumActionsSaved(SmartItem kind)
    {
        var configured = Math.Max(1, Plugin.Config.MinimumKillActionsSavedByStaff.Value);
        return Math.Max(configured, kind == SmartItem.AlternateDimensionStaff ? 2 : 1);
    }

    internal static bool KillStaffSavesEnough(double remainingProgress, double currentCredit,
        SmartItem kind, int bonus)
    {
        if (remainingProgress <= 0 || currentCredit <= 0 || bonus <= 0) return false;
        var before = Math.Ceiling(remainingProgress / currentCredit);
        var after = Math.Ceiling(remainingProgress / (currentCredit + bonus));
        return before - after >= KillStaffMinimumActionsSaved(kind);
    }

    internal static bool IsStrictlyBetter(double candidateUtility, double currentUtility)
    {
        return double.IsFinite(candidateUtility) && double.IsFinite(currentUtility) &&
            candidateUtility > currentUtility + 0.0001;
    }

    internal static int StrategicUnlockBonus(IEnumerable<int> downstreamBonuses)
    {
        return downstreamBonuses?.DefaultIfEmpty(0).Max(x => Math.Max(0, x)) ?? 0;
    }
}

using Xunit;

namespace SmartStrategy;

public sealed class StrategyMathTests
{
    [Theory]
    [InlineData(10, 0.10)]
    [InlineData(65, 0.65)]
    [InlineData(125, 1.00)]
    [InlineData(-5, 0.00)]
    public void PercentageToProbability_ClampsPercentagePoints(double input, double expected)
    {
        Assert.Equal(expected, StrategyMath.PercentageToProbability(input), 8);
    }

    [Fact]
    public void ExpectedEligibleKills_UsesDropDoubleYieldAndGiantBonus()
    {
        var regular = StrategyMath.ExpectedEligibleKills(10, 10, 0, 0, false);
        var giantWithDoubleYield = StrategyMath.ExpectedEligibleKills(10, 10, 20, 50, true);

        Assert.Equal(100, regular, 8);
        Assert.Equal(22.22222222, giantWithDoubleYield, 6);
    }

    [Fact]
    public void ExpectedEligibleKills_HandlesInvalidAndCappedProbabilities()
    {
        Assert.Equal(double.PositiveInfinity, StrategyMath.ExpectedEligibleKills(1, 0, 0, 0, false));
        Assert.Equal(5, StrategyMath.ExpectedEligibleKills(10, 90, 90, 100, true), 8);
        Assert.Equal(0, StrategyMath.ExpectedEligibleKills(0, 10, 0, 0, false));
    }

    [Theory]
    [InlineData(3, 125, false, 128)]
    [InlineData(0, 10, false, 11)]
    [InlineData(50, 125, true, 1)]
    public void ForecastQuestCredit_UsesLiveCreditAndProtectsCollabTasks(
        int liveCredit, int inactiveItemBonus, bool collab, double expected)
    {
        Assert.Equal(expected, StrategyMath.ForecastQuestCredit(liveCredit, inactiveItemBonus, collab), 8);
    }

    [Fact]
    public void CompletionUtility_CombinesOverlappingQuestsAndCapsEachCompletion()
    {
        var first = new PortfolioContribution(1.25, 100, 5, 1);
        var second = new PortfolioContribution(0.9, 20, 2, 1);

        var combined = StrategyMath.PortfolioUtility(new[] { first, second }, 20, 180, 0);
        var firstOnly = StrategyMath.PortfolioUtility(new[] { first }, 20, 180, 0);

        Assert.True(combined > firstOnly);
        Assert.Equal(2.15, combined, 8);
    }

    [Fact]
    public void CompletionUtility_ChargesTravelTimeAndRejectsUnproductiveMaps()
    {
        var useful = new PortfolioContribution(1, 120, 2, 1);
        var impossible = new PortfolioContribution(5, 100, 0, 10);

        var current = StrategyMath.PortfolioUtility(new[] { useful, impossible }, 20, 180, 0);
        var travel = StrategyMath.PortfolioUtility(new[] { useful, impossible }, 20, 180, 90);

        Assert.Equal(1, current, 8);
        Assert.Equal(0.5, travel, 8);
    }

    [Theory]
    [InlineData(60, 300, 100, true)]
    [InlineData(200, 300, 100, false)]
    [InlineData(0, 100, 100, false)]
    [InlineData(double.PositiveInfinity, 300, 10, false)]
    public void ShouldFarmForBooster_RequiresStrictNetTimeSaving(
        double farmSeconds, double directSeconds, double boostedSeconds, bool expected)
    {
        Assert.Equal(expected, StrategyMath.ShouldFarmForBooster(farmSeconds, directSeconds, boostedSeconds));
    }

    [Theory]
    [InlineData(4.0, 3.0, true)]
    [InlineData(3.0, 3.0, false)]
    [InlineData(double.NaN, 2.0, false)]
    public void IsStrictlyBetter_OnlyAcceptsFiniteImprovement(double candidate, double current, bool expected)
    {
        Assert.Equal(expected, StrategyMath.IsStrictlyBetter(candidate, current));
    }

    [Theory]
    [InlineData(new int[] { }, 0)]
    [InlineData(new[] { 120, 240, 180 }, 240)]
    [InlineData(new[] { -5, 0 }, 0)]
    public void StrategicUnlockBonus_UsesHighestDownstreamDependency(int[] bonuses, int expected)
    {
        Assert.Equal(expected, StrategyMath.StrategicUnlockBonus(bonuses));
    }
}

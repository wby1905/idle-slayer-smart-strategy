namespace SmartStrategy;

/// <summary>
/// Version-sensitive gameplay values that are not exposed by a stable runtime API.
/// Planning and execution must read them from this single model.
/// </summary>
internal static class CraftableEffectModel
{
    internal const double CollectablesQuestProgressBonus = 8;
    internal const double RandomBoxQuestProgressBonus = 5;
    internal const double RandomStoneProgressMultiplier = 1.4;
    internal const double RagePillPlanningMultiplier = 4;

    internal static bool IsKillStaff(SmartItem kind) =>
        kind is SmartItem.AlternateDimensionStaff or
            SmartItem.BidimensionalStaff or
            SmartItem.DeathwaveScepter;

    internal static int KillProgressBonus(SmartItem kind) => kind switch
    {
        SmartItem.AlternateDimensionStaff => 10,
        SmartItem.BidimensionalStaff => 50,
        SmartItem.DeathwaveScepter => 125,
        _ => 0
    };

    internal static IEnumerable<(SmartItem Kind, int Bonus)> KillStaffsAscending()
    {
        yield return (SmartItem.AlternateDimensionStaff,
            KillProgressBonus(SmartItem.AlternateDimensionStaff));
        yield return (SmartItem.BidimensionalStaff,
            KillProgressBonus(SmartItem.BidimensionalStaff));
        yield return (SmartItem.DeathwaveScepter,
            KillProgressBonus(SmartItem.DeathwaveScepter));
    }

    internal static IEnumerable<(SmartItem Kind, int Bonus)> KillStaffsDescending()
    {
        yield return (SmartItem.DeathwaveScepter,
            KillProgressBonus(SmartItem.DeathwaveScepter));
        yield return (SmartItem.BidimensionalStaff,
            KillProgressBonus(SmartItem.BidimensionalStaff));
        yield return (SmartItem.AlternateDimensionStaff,
            KillProgressBonus(SmartItem.AlternateDimensionStaff));
    }
}

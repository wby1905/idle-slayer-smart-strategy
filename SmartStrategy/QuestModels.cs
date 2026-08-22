using Il2Cpp;

namespace SmartStrategy;

internal enum QuestSource
{
    Normal,
    Daily,
    Weekly
}

internal sealed class QuestPlan
{
    internal Quest Quest { get; }
    internal QuestSource Source { get; }
    internal BaseMap TargetMap { get; }
    internal int Score { get; }
    internal int Difficulty { get; }
    internal double ExpectedSeconds { get; }
    internal double TargetEnemiesPerPattern { get; }
    internal int JointQuestCount { get; }
    internal int StrategicUnlockBonus { get; }
    internal double MapUtility { get; }
    internal int InstanceId { get; }
    internal int Current => Quest?.questCurrentGoal ?? 0;
    internal int Goal => Quest?.questGoal ?? 0;
    internal QuestType Type => Quest?.questType ?? QuestType.KillEnemies;
    internal bool TargetsGiants => QuestDirector.TargetsGiant(Quest);
    internal bool RequiresRageForBowAutomation => QuestDirector.TargetCannotBeKilledWithArrows(Quest);
    internal bool IsCollab => QuestDirector.IsCollabQuest(Quest);
    internal string Name => string.IsNullOrWhiteSpace(Quest?.localizedName)
        ? Quest?.name ?? "Unknown Quest"
        : Quest.localizedName;

    internal QuestPlan(Quest quest, QuestSource source, BaseMap targetMap, int score, int difficulty,
        double expectedSeconds, double targetEnemiesPerPattern, int jointQuestCount = 0,
        int strategicUnlockBonus = 0, double mapUtility = 0)
    {
        Quest = quest;
        Source = source;
        TargetMap = targetMap;
        Score = score;
        Difficulty = difficulty;
        ExpectedSeconds = expectedSeconds;
        TargetEnemiesPerPattern = targetEnemiesPerPattern;
        JointQuestCount = jointQuestCount;
        StrategicUnlockBonus = strategicUnlockBonus;
        MapUtility = mapUtility;
        InstanceId = quest == null ? 0 : quest.GetInstanceID();
    }
}

internal sealed class PortfolioMapChoice
{
    private readonly Dictionary<int, MapEfficiency> _questEstimates;
    internal BaseMap Map { get; }
    internal double Utility { get; }
    internal int ProgressingQuestCount { get; }

    internal PortfolioMapChoice(BaseMap map, double utility, int progressingQuestCount,
        Dictionary<int, MapEfficiency> questEstimates)
    {
        Map = map;
        Utility = utility;
        ProgressingQuestCount = progressingQuestCount;
        _questEstimates = questEstimates;
    }

    internal bool TryGetEstimate(Quest quest, out MapEfficiency estimate) =>
        _questEstimates.TryGetValue(quest?.GetInstanceID() ?? 0, out estimate);
}

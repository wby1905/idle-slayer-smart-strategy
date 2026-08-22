namespace SmartStrategy;

/// <summary>
/// Names owned by AutoProgression. Keeping them isolated makes optional-mod compatibility auditable.
/// </summary>
internal static class AutoProgressionContract
{
    internal const string ConfigFileName = "AutoProgression.cfg";
    internal const string QuestSectionHeader = "[Quests]";
    internal const string QuestMasterEnabledKey = "Enabled";
    internal const string AscensionServiceTypeName = "AutoProgression.Ascension.AutomaticAscensionService";
    internal const string AscensionTickMethodName = "Tick";
    internal const string WaitingForPostAscensionFieldName = "waitingForPostAscension";

    private static readonly HashSet<string> QuestMutationKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Auto Claim Completed Quests",
        "Regenerate Daily Quests",
        "Regenerate Weekly Quests",
        "Unlimited Quest Rerolls",
        "Prefer 180k Rage Weekly Quest",
        "Filter Generated Daily Quests"
    };

    internal static bool IsQuestMutationKey(string key) =>
        !string.IsNullOrWhiteSpace(key) && QuestMutationKeys.Contains(key);
}

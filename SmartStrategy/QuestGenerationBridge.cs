using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace SmartStrategy;

internal static class QuestGenerationBridge
{
    private static readonly Queue<int[]> DailyBatches = new();
    private static readonly Queue<int[]> WeeklyBatches = new();
    private static HashSet<int> _dailyBeforeGeneration;
    private static HashSet<int> _weeklyBeforeGeneration;
    private static int _dailyGenerationDepth;
    private static int _weeklyGenerationDepth;
    private static int _dailyRerollDepth;
    private static int _weeklyRerollDepth;

    internal static void BeginDailyGeneration()
    {
        if (_dailyRerollDepth > 0) return;
        if (_dailyGenerationDepth++ == 0) _dailyBeforeGeneration = SnapshotDailyIds();
    }

    internal static void EndDailyGeneration()
    {
        if (_dailyRerollDepth > 0 || _dailyGenerationDepth <= 0) return;
        if (--_dailyGenerationDepth > 0) return;
        EnqueueNewIds(DailyBatches, _dailyBeforeGeneration, SnapshotDailyIds());
        _dailyBeforeGeneration = null;
    }

    internal static void BeginWeeklyGeneration()
    {
        if (_weeklyRerollDepth > 0) return;
        if (_weeklyGenerationDepth++ == 0) _weeklyBeforeGeneration = SnapshotWeeklyIds();
    }

    internal static void EndWeeklyGeneration()
    {
        if (_weeklyRerollDepth > 0 || _weeklyGenerationDepth <= 0) return;
        if (--_weeklyGenerationDepth > 0) return;
        EnqueueNewIds(WeeklyBatches, _weeklyBeforeGeneration, SnapshotWeeklyIds());
        _weeklyBeforeGeneration = null;
    }

    internal static void BeginDailyReroll() => _dailyRerollDepth++;
    internal static void EndDailyReroll()
    {
        if (_dailyRerollDepth > 0) _dailyRerollDepth--;
    }

    internal static void BeginWeeklyReroll() => _weeklyRerollDepth++;
    internal static void EndWeeklyReroll()
    {
        if (_weeklyRerollDepth > 0) _weeklyRerollDepth--;
    }

    internal static bool TryDequeueDaily(out int[] ids) => TryDequeue(DailyBatches, out ids);
    internal static bool TryDequeueWeekly(out int[] ids) => TryDequeue(WeeklyBatches, out ids);
    internal static HashSet<int> ActiveDailyIds() => SnapshotDailyIds();
    internal static HashSet<int> ActiveWeeklyIds() => SnapshotWeeklyIds();

    private static bool TryDequeue(Queue<int[]> batches, out int[] ids)
    {
        if (batches.Count > 0)
        {
            ids = batches.Dequeue();
            return true;
        }
        ids = null;
        return false;
    }

    private static void EnqueueNewIds(Queue<int[]> batches, HashSet<int> before, HashSet<int> after)
    {
        var added = after.Where(id => before == null || !before.Contains(id)).ToArray();
        if (added.Length > 0) batches.Enqueue(added);
    }

    private static HashSet<int> SnapshotDailyIds()
    {
        var ids = new HashSet<int>();
        foreach (var quest in Resources.FindObjectsOfTypeAll<DailyQuest>())
        {
            try
            {
                if (quest != null && quest.active && !quest.isClaimed) ids.Add(quest.GetInstanceID());
            }
        catch { /* A native object being replaced is left for the post-generation snapshot. */ }
        }
        return ids;
    }

    private static HashSet<int> SnapshotWeeklyIds()
    {
        var ids = new HashSet<int>();
        foreach (var quest in Resources.FindObjectsOfTypeAll<WeeklyQuest>())
        {
            try
            {
                if (quest != null && quest.active && !quest.isClaimed) ids.Add(quest.GetInstanceID());
            }
        catch { /* A native object being replaced is left for the post-generation snapshot. */ }
        }
        return ids;
    }
}

[HarmonyPatch(typeof(DailyQuestsManager), nameof(DailyQuestsManager.RegenerateDailys))]
internal static class SmartDailyRegenerationPatch
{
    private static void Prefix() => QuestGenerationBridge.BeginDailyGeneration();
    private static Exception Finalizer(Exception __exception)
    {
        QuestGenerationBridge.EndDailyGeneration();
        return __exception;
    }
}

[HarmonyPatch(typeof(DailyQuestsManager), nameof(DailyQuestsManager.AddQuests))]
internal static class SmartDailyAdditionPatch
{
    private static void Prefix() => QuestGenerationBridge.BeginDailyGeneration();
    private static Exception Finalizer(Exception __exception)
    {
        QuestGenerationBridge.EndDailyGeneration();
        return __exception;
    }
}

[HarmonyPatch(typeof(WeeklyQuestsManager), nameof(WeeklyQuestsManager.RegenerateWeeklies))]
internal static class SmartWeeklyRegenerationPatch
{
    private static void Prefix() => QuestGenerationBridge.BeginWeeklyGeneration();
    private static Exception Finalizer(Exception __exception)
    {
        QuestGenerationBridge.EndWeeklyGeneration();
        return __exception;
    }
}

[HarmonyPatch(typeof(WeeklyQuestsManager), nameof(WeeklyQuestsManager.AddQuests))]
internal static class SmartWeeklyAdditionPatch
{
    private static void Prefix() => QuestGenerationBridge.BeginWeeklyGeneration();
    private static Exception Finalizer(Exception __exception)
    {
        QuestGenerationBridge.EndWeeklyGeneration();
        return __exception;
    }
}

[HarmonyPatch(typeof(DailyQuestReroll), nameof(DailyQuestReroll.RewardForShowing))]
internal static class SmartDailyRerollBoundaryPatch
{
    private static void Prefix() => QuestGenerationBridge.BeginDailyReroll();
    private static Exception Finalizer(Exception __exception)
    {
        QuestGenerationBridge.EndDailyReroll();
        return __exception;
    }
}

[HarmonyPatch(typeof(WeeklyQuestReroll), nameof(WeeklyQuestReroll.RewardForShowing))]
internal static class SmartWeeklyRerollBoundaryPatch
{
    private static void Prefix() => QuestGenerationBridge.BeginWeeklyReroll();
    private static Exception Finalizer(Exception __exception)
    {
        QuestGenerationBridge.EndWeeklyReroll();
        return __exception;
    }
}

using Il2Cpp;
using UnityEngine;

namespace SmartStrategy;

// Stable internal keys also preserve persisted calibration names and their historical numeric order.
// Game QuestType is now a DataObject; never cast it to an integer or use legacyQuestType as live state.
internal enum QuestKind
{
    Unknown = -1,
    KillEnemies = 0,
    PickUpCoins = 1,
    PickUpRubies = 2,
    KillEnemiesWithArrows = 3,
    PickUpSapphires = 4,
    KillFlyers = 5,
    PickUpEmeralds = 6,
    DoCriticals = 7,
    HitRandomBoxes = 8,
    HitRandomSilverBoxes = 9,
    KillGiants = 10,
    KillEnemiesWithRageMode = 11,
    Boost = 12,
    CraftTemporaryItems = 13,
    ChestHuntChests = 14,
    BonusStage = 15,
    BonusStageSections = 16,
    GetMaterials = 17,
    PickUpGemstones = 18,
    KillEnemiesWithCharacter = 19,
    KillEnemiesOfType = 20,
    FindArmoryItems = 21,
    GetCoinsFromRandomBox = 22,
    GoThroughPortals = 23,
    CompleteDailyQuests = 24,
    KillShinyEnemies = 25,
    PickUpDiamonds = 26,
    WindDashKills = 27,
    DigDigPiles = 28,
    UseRageMode = 29,
    CompleteAscendingHeights = 30,
    PickUpZyniums = 31,
    HitRandomGoldenBoxes = 32,
    CompleteGrappleRuns = 33,
    VisitLocation = 34,
    KillEnemiesInLocation = 35,
}
internal static class QuestApi
{
    private static readonly Dictionary<int, QuestKind> Kinds = new();
    private static readonly HashSet<int> ReportedUnknownKinds = new();
    private static int _registryId;
    private static float _nextRegistryRefresh;
    private static float _nextFailureLog;

    internal static QuestKind GetKind(this Quest quest)
    {
        if (quest == null) return QuestKind.Unknown;
        try
        {
            var type = quest.type;
            if (type == null) return QuestKind.Unknown;
            var registry = QuestTypes.list;
            if (registry == null) return QuestKind.Unknown;
            var id = type.GetInstanceID();
            var registryChanged = registry.GetInstanceID() != _registryId;
            if (registryChanged || !Kinds.ContainsKey(id) && Time.unscaledTime >= _nextRegistryRefresh)
                Rebuild(registry);
            if (Kinds.TryGetValue(id, out var kind)) return kind;
            if (ReportedUnknownKinds.Add(id))
                SmartLog.Warning(SmartLogCategory.Compatibility,
                    $"Unsupported quest kind '{type.id ?? type.name}'; leaving it to the native game without planning, rerolling, or crafting.");
            return QuestKind.Unknown;
        }
        catch (Exception ex)
        {
            if (Time.unscaledTime >= _nextFailureLog)
            {
                _nextFailureLog = Time.unscaledTime + 30f;
                SmartLog.Warning(SmartLogCategory.Compatibility,
                    $"Quest type resolution is unavailable; skipping affected quests: {ex.GetType().Name}: {ex.Message}");
            }
            return QuestKind.Unknown;
        }
    }

    internal static bool IsSupported(Quest quest)
    {
        if (quest == null) return false;
        var kind = quest.GetKind();
        return kind != QuestKind.Unknown && (!IsLocationQuest(kind) || quest.map != null);
    }

    internal static bool IsLocationQuest(QuestKind kind) =>
        kind is QuestKind.VisitLocation or QuestKind.KillEnemiesInLocation;

    // Native MapVisited and the KillEnemiesInLocation branch compare this explicit map. Merely
    // belonging to a map event does not constrain ordinary kills or collections to the event map.
    internal static BaseMap RequiredMap(Quest quest) =>
        quest != null && IsLocationQuest(quest.GetKind()) ? quest.map : null;

    internal static bool CanProgressOnMap(Quest quest, BaseMap map) =>
        quest != null && (!IsLocationQuest(quest.GetKind()) ||
                         MapEfficiencyEstimator.SameMap(RequiredMap(quest), map));

    internal static bool EventAvailable(Quest quest) =>
        quest != null && (quest.mapEvent == null || quest.EventAvailable());

    internal static bool CanProgressNow(Quest quest)
    {
        try { return IsSupported(quest) && EventAvailable(quest) && quest.CanBeCompleted(); }
        catch { return false; }
    }

    internal static bool UsesUnboostedProgress(Quest quest) =>
        QuestEffectPolicy.ReadCreditRule(quest) != QuestCreditRule.ProgressBonuses;

    internal static void Reset()
    {
        Kinds.Clear();
        ReportedUnknownKinds.Clear();
        _registryId = 0;
        _nextRegistryRefresh = 0;
    }

    private static void Rebuild(QuestTypes registry)
    {
        Kinds.Clear();
        _registryId = registry.GetInstanceID();
        _nextRegistryRefresh = Time.unscaledTime + 5f;
        Add(registry.KillEnemies, QuestKind.KillEnemies);
        Add(registry.PickUpCoins, QuestKind.PickUpCoins);
        Add(registry.PickUpRubies, QuestKind.PickUpRubies);
        Add(registry.KillEnemiesWithArrows, QuestKind.KillEnemiesWithArrows);
        Add(registry.PickUpSapphires, QuestKind.PickUpSapphires);
        Add(registry.KillFlyers, QuestKind.KillFlyers);
        Add(registry.PickUpEmeralds, QuestKind.PickUpEmeralds);
        Add(registry.DoCriticals, QuestKind.DoCriticals);
        Add(registry.HitRandomBoxes, QuestKind.HitRandomBoxes);
        Add(registry.HitRandomSilverBoxes, QuestKind.HitRandomSilverBoxes);
        Add(registry.KillGiants, QuestKind.KillGiants);
        Add(registry.KillEnemiesWithRageMode, QuestKind.KillEnemiesWithRageMode);
        Add(registry.Boost, QuestKind.Boost);
        Add(registry.CraftTemporaryItems, QuestKind.CraftTemporaryItems);
        Add(registry.ChestHuntChests, QuestKind.ChestHuntChests);
        Add(registry.BonusStage, QuestKind.BonusStage);
        Add(registry.BonusStageSections, QuestKind.BonusStageSections);
        Add(registry.GetMaterials, QuestKind.GetMaterials);
        Add(registry.PickUpGemstones, QuestKind.PickUpGemstones);
        Add(registry.KillEnemiesWithCharacter, QuestKind.KillEnemiesWithCharacter);
        Add(registry.KillEnemiesOfType, QuestKind.KillEnemiesOfType);
        Add(registry.FindArmoryItems, QuestKind.FindArmoryItems);
        Add(registry.GetCoinsFromRandomBox, QuestKind.GetCoinsFromRandomBox);
        Add(registry.GoThroughPortals, QuestKind.GoThroughPortals);
        Add(registry.CompleteDailyQuests, QuestKind.CompleteDailyQuests);
        Add(registry.KillShinyEnemies, QuestKind.KillShinyEnemies);
        Add(registry.PickUpDiamonds, QuestKind.PickUpDiamonds);
        Add(registry.WindDashKills, QuestKind.WindDashKills);
        Add(registry.DigDigPiles, QuestKind.DigDigPiles);
        Add(registry.UseRageMode, QuestKind.UseRageMode);
        Add(registry.CompleteAscendingHeights, QuestKind.CompleteAscendingHeights);
        Add(registry.PickUpZyniums, QuestKind.PickUpZyniums);
        Add(registry.HitRandomGoldenBoxes, QuestKind.HitRandomGoldenBoxes);
        Add(registry.CompleteGrappleRuns, QuestKind.CompleteGrappleRuns);
        Add(registry.VisitLocation, QuestKind.VisitLocation);
        Add(registry.KillEnemiesInLocation, QuestKind.KillEnemiesInLocation);
    }

    private static void Add(Il2Cpp.QuestType type, QuestKind kind)
    {
        if (type != null) Kinds[type.GetInstanceID()] = kind;
    }
}

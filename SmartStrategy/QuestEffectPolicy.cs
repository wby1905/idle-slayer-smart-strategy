using Il2Cpp;

namespace SmartStrategy;

internal enum QuestCreditRule { Unknown, RawActions, ProgressBonuses }

// One policy for estimates, recipe supply and the final craft call. Event names/dates are not
// contracts: reuse native quest types, source markers and eligibility for future event content.
// Apply known rules across game updates; an unrecognized task only skips its own item planning.
internal static class QuestEffectPolicy
{
    private static readonly SmartItem[] Candidates =
    {
        SmartItem.ShardsNecklace, SmartItem.DeathwaveScepter, SmartItem.BidimensionalStaff,
        SmartItem.AlternateDimensionStaff, SmartItem.NecklaceOfCollectables, SmartItem.RandomBoxStaff,
        SmartItem.Specialization, SmartItem.RandomStone, SmartItem.Whetstone, SmartItem.KeyManifest,
        SmartItem.CriticalSword, SmartItem.RagePill
    };

    internal static QuestCreditRule ReadCreditRule(Quest quest)
    {
        if (quest == null) return QuestCreditRule.Unknown;
        try
        {
            var kind = quest.GetKind();
            if (kind == QuestKind.Unknown) return QuestCreditRule.Unknown;
            if (quest.mapEvent != null) return QuestCreditRule.RawActions;
            if (quest.fromCollab != null && kind is not (QuestKind.KillEnemiesWithRageMode or QuestKind.WindDashKills))
                return QuestCreditRule.RawActions;
            return QuestCreditRule.ProgressBonuses;
        }
        catch { return QuestCreditRule.Unknown; }
    }

    internal static bool CanAffect(Quest quest, SmartItem item)
    {
        try
        {
            if (!QuestApi.IsSupported(quest)) return false;
            var rule = ReadCreditRule(quest);
            if (rule == QuestCreditRule.Unknown) return false;
            var kind = quest.GetKind();
            if (kind == QuestKind.WindDashKills) return false; // Wind Dash task automation remains passive.
            // Crafting itself is the action for this objective, independent of an item's buff.
            if (kind == QuestKind.CraftTemporaryItems) return item is SmartItem.RandomStone or SmartItem.Whetstone;
            return item switch
            {
                SmartItem.AlternateDimensionStaff or SmartItem.BidimensionalStaff or SmartItem.DeathwaveScepter =>
                    rule == QuestCreditRule.ProgressBonuses && IsKill(kind),
                SmartItem.NecklaceOfCollectables => rule == QuestCreditRule.ProgressBonuses && IsCollectable(kind),
                SmartItem.RandomBoxStaff => rule == QuestCreditRule.ProgressBonuses &&
                    kind is QuestKind.HitRandomBoxes or QuestKind.HitRandomSilverBoxes or QuestKind.HitRandomGoldenBoxes,
                // These change real opportunities/actions, not the credit per action. Raw-count
                // event quests can still benefit when their target actually uses that opportunity.
                SmartItem.ShardsNecklace => IsKill(kind) && QuestDirector.TargetsGiant(quest),
                SmartItem.Whetstone => kind == QuestKind.GetMaterials,
                SmartItem.CriticalSword => kind == QuestKind.DoCriticals,
                SmartItem.RandomStone => kind is QuestKind.HitRandomBoxes or QuestKind.GetCoinsFromRandomBox or
                    QuestKind.BonusStage or QuestKind.BonusStageSections,
                SmartItem.Specialization => kind is QuestKind.BonusStage or QuestKind.BonusStageSections,
                SmartItem.KeyManifest => kind == QuestKind.ChestHuntChests,
                SmartItem.RagePill => kind is QuestKind.KillEnemiesWithRageMode or QuestKind.UseRageMode ||
                    IsKill(kind) && QuestDirector.TargetCannotBeKilledWithArrows(quest),
                _ => false
            };
        }
        catch { return false; }
    }

    internal static bool CanSpend(Quest quest, SmartItem item) =>
        QuestApi.CanProgressNow(quest) && quest.questCurrentGoal < quest.questGoal && CanAffect(quest, item);

    internal static bool CanUsePhysicalAction(Quest quest, QuestKind expected) =>
        expected != QuestKind.Unknown && quest?.GetKind() == expected && QuestApi.CanProgressNow(quest) &&
        quest.questCurrentGoal < quest.questGoal;

    internal static IEnumerable<SmartItem> ApplicableItems(Quest quest)
    {
        // Crafting an item can satisfy a craft objective, but its spawn/drop bonus does not
        // multiply craft credit. Keep the existing stock-only fallback out of booster economics.
        if (quest?.GetKind() == QuestKind.CraftTemporaryItems) yield break;
        foreach (var candidate in Candidates)
            if (CanAffect(quest, candidate)) yield return candidate;
    }

    internal static bool IsDirectCreditItem(SmartItem item) => CraftableEffectModel.IsKillStaff(item) ||
        item is SmartItem.NecklaceOfCollectables or SmartItem.RandomBoxStaff;

    internal static string Describe(Quest quest) => ReadCreditRule(quest) switch
    {
        QuestCreditRule.RawActions => "raw actions; direct quest-credit items disabled",
        QuestCreditRule.ProgressBonuses => "native progress bonuses",
        _ => "unrecognized task; item planning skipped for this task"
    };
    private static bool IsKill(QuestKind kind) => kind is QuestKind.KillEnemies or QuestKind.KillEnemiesWithArrows or
        QuestKind.KillFlyers or QuestKind.KillGiants or QuestKind.KillEnemiesWithRageMode or
        QuestKind.KillEnemiesWithCharacter or QuestKind.KillEnemiesOfType or QuestKind.KillEnemiesInLocation or
        QuestKind.KillShinyEnemies or QuestKind.WindDashKills;
    private static bool IsCollectable(QuestKind kind) => kind is QuestKind.PickUpCoins or QuestKind.PickUpRubies or
        QuestKind.PickUpSapphires or QuestKind.PickUpEmeralds or QuestKind.PickUpGemstones or
        QuestKind.PickUpDiamonds or QuestKind.PickUpZyniums;
}

using System.Globalization;
using Il2Cpp;
using UnityEngine;

namespace SmartStrategy;

internal enum SmartSettingsLanguage
{
    English,
    SimplifiedChinese
}

internal enum SmartSettingsText
{
    PanelButton,
    Title,
    Subtitle,
    Apply,
    Cancel,
    UiNotReady,
    CloseCurrentPopup,
    OpenFailed,
    NoChanges,
    ChangesApplied,
    ApplyFailed,
    EndgameRiskDescription,
    EndgameEnabledWarning,
    AutoProgressionQuestConflict,

    SectionBasic,
    SectionQuests,
    SectionCombatAndEvents,
    SectionMaterialsAndTravel,
    SectionEndgame,

    Enabled,
    QuestAutomation,
    MaterialTravel,
    ResetPortalCooldown,
    AutomaticCombat,
    EndgameMode,
    AutoClaim,
    RegenerateDaily,
    RegenerateWeekly,
    RerollDaily,
    RerollWeekly,
    WeeklyPair,
    AdaptiveDaily,
    JointMap,
    StrategicUnlock,
    CraftQuestEggs,
    QuestSupply,
    AutomaticJump,
    AutomaticBoost,
    GiantRapidAttack,
    RageWheneverReady,
    HordeRage,
    SoulRagePill,
    QuestRagePill,
    ChestPreparation,
    ChestWithoutSoulEvent,
    ChestStack,
    WhetstoneRecovery,
    FillWhetstone,
    BoosterLinger,
    RareReserves,
    IdleSurplus,
    IdleSoulMap,
    MaterialQuestCapacity,
    OpportunisticMaterials,
    PermanentMultipliers,
    PermanentSouls,
    PermanentCraftables,
    PermanentPaid500
}

internal static class SmartSettingsLocalization
{
    private static readonly IReadOnlyDictionary<SmartSettingsText, string> English =
        new Dictionary<SmartSettingsText, string>
        {
            [SmartSettingsText.PanelButton] = "Smart Strategy",
            [SmartSettingsText.Title] = "Smart Strategy",
            [SmartSettingsText.Subtitle] = "Changes remain a draft until you apply them",
            [SmartSettingsText.Apply] = "Apply",
            [SmartSettingsText.Cancel] = "Cancel",
            [SmartSettingsText.UiNotReady] = "Smart Strategy settings are not ready yet",
            [SmartSettingsText.CloseCurrentPopup] = "Close the current popup first",
            [SmartSettingsText.OpenFailed] = "Unable to open Smart Strategy settings. Check the log for details.",
            [SmartSettingsText.NoChanges] = "No Smart Strategy settings were changed",
            [SmartSettingsText.ChangesApplied] = "Applied and saved {0} Smart Strategy settings",
            [SmartSettingsText.ApplyFailed] = "Apply failed. Your changes were not saved.",
            [SmartSettingsText.EndgameRiskDescription] = "Warning: Endgame mode directly maintains client-side timed effects, including 500x paid-style bonuses, and materially changes progression. It is off by default. Back up your save and enable it only if you accept a nonstandard endgame experience.",
            [SmartSettingsText.EndgameEnabledWarning] = "Endgame mode is enabled. Client timers and 500x effects can now alter progression; press F10 if this was unintended.",
            [SmartSettingsText.AutoProgressionQuestConflict] = "AutoProgression quest automation is still enabled and may conflict with Smart Strategy",

            [SmartSettingsText.SectionBasic] = "Basic Controls",
            [SmartSettingsText.SectionQuests] = "Quest Strategy",
            [SmartSettingsText.SectionCombatAndEvents] = "Combat & Events",
            [SmartSettingsText.SectionMaterialsAndTravel] = "Materials & Travel",
            [SmartSettingsText.SectionEndgame] = "Endgame & Permanent Bonuses",

            [SmartSettingsText.Enabled] = "Enable Smart Strategy",
            [SmartSettingsText.QuestAutomation] = "Automate quests",
            [SmartSettingsText.MaterialTravel] = "Automate material travel",
            [SmartSettingsText.ResetPortalCooldown] = "Keep the Portal cooldown at zero",
            [SmartSettingsText.AutomaticCombat] = "Automate movement and smart combat",
            [SmartSettingsText.EndgameMode] = "Endgame mode",
            [SmartSettingsText.AutoClaim] = "Claim completed quests automatically",
            [SmartSettingsText.RegenerateDaily] = "Generate a new daily set when none is available",
            [SmartSettingsText.RegenerateWeekly] = "Generate a new weekly set when none is available",
            [SmartSettingsText.RerollDaily] = "Evaluate and reroll daily quests without limits",
            [SmartSettingsText.RerollWeekly] = "Evaluate and reroll weekly quests without limits",
            [SmartSettingsText.WeeklyPair] = "Search weeklies in Daily Quests then Portals phases",
            [SmartSettingsText.AdaptiveDaily] = "Learn the daily reroll threshold from completion history",
            [SmartSettingsText.JointMap] = "Choose the best map for multiple quests together",
            [SmartSettingsText.StrategicUnlock] = "Prioritize important unlock quests",
            [SmartSettingsText.CraftQuestEggs] = "Allow real Dragon Eggs for craft quests",
            [SmartSettingsText.QuestSupply] = "Restock for quests only when the net benefit is positive",
            [SmartSettingsText.AutomaticJump] = "Jump automatically",
            [SmartSettingsText.AutomaticBoost] = "Use regular boost automatically",
            [SmartSettingsText.GiantRapidAttack] = "Attack Giants rapidly",
            [SmartSettingsText.RageWheneverReady] = "Use Rage Mode whenever its cooldown is ready",
            [SmartSettingsText.HordeRage] = "Use natural Rage Mode during Horde events",
            [SmartSettingsText.SoulRagePill] = "Use Rage Pills to extend Rage during Souls events",
            [SmartSettingsText.QuestRagePill] = "Use Rage Pills for Rage quests when worthwhile",
            [SmartSettingsText.ChestPreparation] = "Prepare Souls bonuses before Chest Hunt",
            [SmartSettingsText.ChestWithoutSoulEvent] = "Prepare Chest Hunt bonuses without a Souls event",
            [SmartSettingsText.ChestStack] = "Use both Spyglass and Compass before Chest Hunt",
            [SmartSettingsText.WhetstoneRecovery] = "Maintain Whetstone while farming materials",
            [SmartSettingsText.FillWhetstone] = "Fill Whetstone materials to capacity after restocking starts",
            [SmartSettingsText.BoosterLinger] = "Wait for material bonuses to expire when the next phase needs no materials",
            [SmartSettingsText.RareReserves] = "Protect safety reserves of rare materials",
            [SmartSettingsText.IdleSurplus] = "Fill common materials while idle",
            [SmartSettingsText.IdleSoulMap] = "Travel to the best Souls map after materials are full",
            [SmartSettingsText.MaterialQuestCapacity] = "Craft items to free capacity for material quests",
            [SmartSettingsText.OpportunisticMaterials] = "Collect missing materials while on quest maps",
            [SmartSettingsText.PermanentMultipliers] = "Keep free random multipliers active",
            [SmartSettingsText.PermanentSouls] = "Keep the free Souls multiplier active",
            [SmartSettingsText.PermanentCraftables] = "Keep timed craftable effects active",
            [SmartSettingsText.PermanentPaid500] = "Keep 500x Souls and Coins multipliers active"
        };

    private static readonly IReadOnlyDictionary<SmartSettingsText, string> SimplifiedChinese =
        new Dictionary<SmartSettingsText, string>
        {
            [SmartSettingsText.PanelButton] = "智能策略",
            [SmartSettingsText.Title] = "智能策略",
            [SmartSettingsText.Subtitle] = "修改仅保存在本页草稿中，点击应用后才会生效",
            [SmartSettingsText.Apply] = "应用",
            [SmartSettingsText.Cancel] = "取消",
            [SmartSettingsText.UiNotReady] = "智能策略设置界面尚未就绪",
            [SmartSettingsText.CloseCurrentPopup] = "请先关闭当前弹窗",
            [SmartSettingsText.OpenFailed] = "智能策略设置打开失败，请检查日志",
            [SmartSettingsText.NoChanges] = "智能策略设置没有变化",
            [SmartSettingsText.ChangesApplied] = "已应用并保存 {0} 项智能策略设置",
            [SmartSettingsText.ApplyFailed] = "应用失败，本次修改未生效",
            [SmartSettingsText.EndgameRiskDescription] = "警告：终局模式会直接维持客户端计时效果，包括 500x 付费样式倍率，并显著改变成长节奏。该模式默认关闭。请先备份存档，确认接受非标准终局体验后再开启。",
            [SmartSettingsText.EndgameEnabledWarning] = "终局模式已开启：客户端计时器和 500x 倍率将改变成长进程；如果并非有意开启，请立即按 F10。",
            [SmartSettingsText.AutoProgressionQuestConflict] = "AutoProgression 仍有任务子项开启，可能与智能策略冲突",

            [SmartSettingsText.SectionBasic] = "基础控制",
            [SmartSettingsText.SectionQuests] = "任务策略",
            [SmartSettingsText.SectionCombatAndEvents] = "战斗与事件",
            [SmartSettingsText.SectionMaterialsAndTravel] = "材料与路线",
            [SmartSettingsText.SectionEndgame] = "终局与常驻收益",

            [SmartSettingsText.Enabled] = "智能策略总开关",
            [SmartSettingsText.QuestAutomation] = "自动任务",
            [SmartSettingsText.MaterialTravel] = "自动材料旅行",
            [SmartSettingsText.ResetPortalCooldown] = "持续重置传送门冷却",
            [SmartSettingsText.AutomaticCombat] = "自动移动与智能战斗",
            [SmartSettingsText.EndgameMode] = "终局模式",
            [SmartSettingsText.AutoClaim] = "自动领取已完成任务",
            [SmartSettingsText.RegenerateDaily] = "没有日常时自动生成新一组",
            [SmartSettingsText.RegenerateWeekly] = "没有周常时自动生成新一组",
            [SmartSettingsText.RerollDaily] = "自动筛选并无限刷新日常",
            [SmartSettingsText.RerollWeekly] = "自动筛选并无限刷新周常",
            [SmartSettingsText.WeeklyPair] = "周常按每日任务 → 传送门分阶段搜索",
            [SmartSettingsText.AdaptiveDaily] = "根据实际完成历史学习日常刷新线",
            [SmartSettingsText.JointMap] = "联合多项任务选择最优地图",
            [SmartSettingsText.StrategicUnlock] = "优先完成关键解锁任务",
            [SmartSettingsText.CraftQuestEggs] = "制作任务允许使用真实龙蛋库存",
            [SmartSettingsText.QuestSupply] = "按净收益决定是否为任务补料",
            [SmartSettingsText.AutomaticJump] = "自动跳跃",
            [SmartSettingsText.AutomaticBoost] = "自动普通冲刺",
            [SmartSettingsText.GiantRapidAttack] = "巨人快速攻击",
            [SmartSettingsText.RageWheneverReady] = "狂暴冷却完成后自动使用",
            [SmartSettingsText.HordeRage] = "怪群事件自动使用自然狂暴",
            [SmartSettingsText.SoulRagePill] = "灵魂事件允许药丸续狂暴",
            [SmartSettingsText.QuestRagePill] = "狂暴任务允许按收益使用药丸",
            [SmartSettingsText.ChestPreparation] = "宝箱小游戏前准备灵魂增益",
            [SmartSettingsText.ChestWithoutSoulEvent] = "没有灵魂事件也准备宝箱增益",
            [SmartSettingsText.ChestStack] = "宝箱前同时使用望远镜与罗盘",
            [SmartSettingsText.WhetstoneRecovery] = "材料地图自动维持磨刀石",
            [SmartSettingsText.FillWhetstone] = "磨刀石补料触发后刷到材料上限",
            [SmartSettingsText.BoosterLinger] = "下阶段不刷材料时等待增益结束",
            [SmartSettingsText.RareReserves] = "保护稀有材料安全库存",
            [SmartSettingsText.IdleSurplus] = "空闲时补满普通材料",
            [SmartSettingsText.IdleSoulMap] = "材料补满后前往最高灵魂地图",
            [SmartSettingsText.MaterialQuestCapacity] = "材料任务满仓时制作物品腾出容量",
            [SmartSettingsText.OpportunisticMaterials] = "任务地图顺便收集未满材料",
            [SmartSettingsText.PermanentMultipliers] = "常驻免费随机倍率",
            [SmartSettingsText.PermanentSouls] = "常驻免费灵魂倍率",
            [SmartSettingsText.PermanentCraftables] = "常驻有持续时间的制作物效果",
            [SmartSettingsText.PermanentPaid500] = "常驻 500x 灵魂与金币倍率"
        };

    internal static SmartSettingsLanguage CurrentLanguage
    {
        get
        {
            try
            {
                var gameLanguage = LocalizationSystem.currentLanguage;
                if (gameLanguage != null && gameLanguage.language == SystemLanguage.ChineseSimplified)
                    return SmartSettingsLanguage.SimplifiedChinese;
            }
            catch
            {
                // The localization system is unavailable while the game scene is loading.
            }

            return SmartSettingsLanguage.English;
        }
    }

    internal static string Text(SmartSettingsText key) => Text(CurrentLanguage, key);

    internal static string Text(SmartSettingsLanguage language, SmartSettingsText key)
    {
        var selected = language == SmartSettingsLanguage.SimplifiedChinese
            ? SimplifiedChinese
            : English;
        if (selected.TryGetValue(key, out var translated) && !string.IsNullOrWhiteSpace(translated))
            return translated;
        if (English.TryGetValue(key, out var fallback) && !string.IsNullOrWhiteSpace(fallback))
            return fallback;
        return key.ToString();
    }

    internal static string Format(SmartSettingsText key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Text(key), args);
}

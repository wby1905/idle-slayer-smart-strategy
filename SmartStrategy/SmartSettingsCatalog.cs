using MelonLoader;

namespace SmartStrategy;

internal enum SmartSettingId
{
    Enabled,
    QuestAutomation,
    MaterialTravel,
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
    ResetPortalCooldown,
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
    PermanentPaid500,
    BunnyPaw,
    DeathBringer,
    CpsSpyglass,
    SupplementalRefining,
    RefineryRawFarming,
    RefineryIdleStock,
    RefineryBuySlots,
    RefinerySpeedup
}

internal static partial class SmartSettingsUi
{
    private sealed class Definition
    {
        internal readonly SmartSettingId Key;
        internal readonly SmartSettingsText Section;
        internal readonly SmartSettingsText Label;
        internal readonly MelonPreferences_Entry<bool> Entry;
        internal readonly SmartSettingId[] Requires;

        internal Definition(SmartSettingId key, SmartSettingsText section, SmartSettingsText label,
            MelonPreferences_Entry<bool> entry, params SmartSettingId[] requires)
        {
            Key = key;
            Section = section;
            Label = label;
            Entry = entry;
            Requires = requires ?? Array.Empty<SmartSettingId>();
        }
    }

    private static List<Definition> _definitions;

    private static IReadOnlyList<Definition> Definitions() => _definitions ??= BuildDefinitions();

    private static List<Definition> BuildDefinitions()
    {
        var c = Plugin.Config;
        var basic = SmartSettingsText.SectionBasic;
        var quests = SmartSettingsText.SectionQuests;
        var combat = SmartSettingsText.SectionCombatAndEvents;
        var materials = SmartSettingsText.SectionMaterialsAndTravel;
        var endgame = SmartSettingsText.SectionEndgame;
        return new List<Definition>
        {
            new(SmartSettingId.Enabled, basic, SmartSettingsText.Enabled, c.Enabled),
            new(SmartSettingId.QuestAutomation, basic, SmartSettingsText.QuestAutomation, c.QuestAutomationEnabled),
            new(SmartSettingId.MaterialTravel, basic, SmartSettingsText.MaterialTravel, c.AutomaticMaterialTravel),
            new(SmartSettingId.AutomaticCombat, basic, SmartSettingsText.AutomaticCombat, c.AutomaticCombatEnabled),
            new(SmartSettingId.EndgameMode, basic, SmartSettingsText.EndgameMode, c.EndgameModeEnabled),

            new(SmartSettingId.AutoClaim, quests, SmartSettingsText.AutoClaim, c.AutoClaimQuests, SmartSettingId.QuestAutomation),
            new(SmartSettingId.RegenerateDaily, quests, SmartSettingsText.RegenerateDaily, c.AutoRegenerateDailyQuests, SmartSettingId.QuestAutomation),
            new(SmartSettingId.RegenerateWeekly, quests, SmartSettingsText.RegenerateWeekly, c.AutoRegenerateWeeklyQuests, SmartSettingId.QuestAutomation),
            new(SmartSettingId.RerollDaily, quests, SmartSettingsText.RerollDaily, c.AutoRerollDailyQuests, SmartSettingId.QuestAutomation),
            new(SmartSettingId.RerollWeekly, quests, SmartSettingsText.RerollWeekly, c.AutoRerollWeeklyQuests, SmartSettingId.QuestAutomation),
            new(SmartSettingId.WeeklyPair, quests, SmartSettingsText.WeeklyPair, c.WeeklyPreferredPairStrategy, SmartSettingId.QuestAutomation, SmartSettingId.RerollWeekly),
            new(SmartSettingId.AdaptiveDaily, quests, SmartSettingsText.AdaptiveDaily, c.AdaptiveDailyRerollThresholdEnabled, SmartSettingId.QuestAutomation, SmartSettingId.RerollDaily),
            new(SmartSettingId.JointMap, quests, SmartSettingsText.JointMap, c.JointMapPlanningEnabled, SmartSettingId.QuestAutomation),
            new(SmartSettingId.StrategicUnlock, quests, SmartSettingsText.StrategicUnlock, c.StrategicUnlockPlanningEnabled, SmartSettingId.QuestAutomation),
            new(SmartSettingId.CraftQuestEggs, quests, SmartSettingsText.CraftQuestEggs, c.CraftQuestUseDragonEggs, SmartSettingId.QuestAutomation),
            new(SmartSettingId.QuestSupply, quests, SmartSettingsText.QuestSupply, c.QuestSupplyPlanningEnabled, SmartSettingId.QuestAutomation),

            new(SmartSettingId.AutomaticJump, combat, SmartSettingsText.AutomaticJump, c.AutomaticJumpEnabled, SmartSettingId.AutomaticCombat),
            new(SmartSettingId.AutomaticBoost, combat, SmartSettingsText.AutomaticBoost, c.AutomaticBoostEnabled, SmartSettingId.AutomaticCombat),
            new(SmartSettingId.GiantRapidAttack, combat, SmartSettingsText.GiantRapidAttack, c.GiantRapidAttackEnabled, SmartSettingId.AutomaticCombat),
            new(SmartSettingId.RageWheneverReady, combat, SmartSettingsText.RageWheneverReady, c.UseRageWheneverReady),
            new(SmartSettingId.HordeRage, combat, SmartSettingsText.HordeRage, c.RageDuringHordeEventEnabled),
            new(SmartSettingId.SoulRagePill, combat, SmartSettingsText.SoulRagePill, c.RagePillEnabled),
            new(SmartSettingId.QuestRagePill, combat, SmartSettingsText.QuestRagePill, c.QuestRagePillEnabled, SmartSettingId.QuestAutomation),
            new(SmartSettingId.BunnyPaw, combat, SmartSettingsText.BunnyPaw, c.BunnyPawEnabled),
            new(SmartSettingId.DeathBringer, combat, SmartSettingsText.DeathBringer, c.DeathBringerEnabled),
            new(SmartSettingId.CpsSpyglass, combat, SmartSettingsText.CpsSpyglass, c.CpsSpyglassEnabled),
            new(SmartSettingId.ChestPreparation, combat, SmartSettingsText.ChestPreparation, c.CraftBeforeChestHunt),
            new(SmartSettingId.ChestWithoutSoulEvent, combat, SmartSettingsText.ChestWithoutSoulEvent, c.BoostChestHuntWithoutSoulsEvent, SmartSettingId.ChestPreparation),
            new(SmartSettingId.ChestStack, combat, SmartSettingsText.ChestStack, c.StackCompassWithSpyglassForChestHunt, SmartSettingId.ChestPreparation),

            new(SmartSettingId.WhetstoneRecovery, materials, SmartSettingsText.WhetstoneRecovery, c.WhetstoneDuringMaterialRecovery),
            new(SmartSettingId.SupplementalRefining, materials, SmartSettingsText.SupplementalRefining, c.SupplementalRefiningEnabled),
            new(SmartSettingId.RefineryRawFarming, materials, SmartSettingsText.RefineryRawFarming, c.RefineryRawMaterialFarming, SmartSettingId.SupplementalRefining, SmartSettingId.MaterialTravel),
            new(SmartSettingId.RefineryIdleStock, materials, SmartSettingsText.RefineryIdleStock, c.RefineryIdleStockEnabled, SmartSettingId.SupplementalRefining),
            new(SmartSettingId.RefineryBuySlots, materials, SmartSettingsText.RefineryBuySlots, c.RefineryBuySlotsEnabled, SmartSettingId.SupplementalRefining),
            new(SmartSettingId.RefinerySpeedup, materials, SmartSettingsText.RefinerySpeedup, c.RefinerySpeedupEnabled, SmartSettingId.SupplementalRefining),
            new(SmartSettingId.ResetPortalCooldown, materials, SmartSettingsText.ResetPortalCooldown, c.ResetPortalCooldown),
            new(SmartSettingId.FillWhetstone, materials, SmartSettingsText.FillWhetstone, c.FillWhetstoneMaterialsToCapacity, SmartSettingId.MaterialTravel),
            new(SmartSettingId.BoosterLinger, materials, SmartSettingsText.BoosterLinger, c.MaterialBoosterLingerEnabled),
            new(SmartSettingId.RareReserves, materials, SmartSettingsText.RareReserves, c.RareMaterialReservesEnabled),
            new(SmartSettingId.IdleSurplus, materials, SmartSettingsText.IdleSurplus, c.IdleSurplusMaterialFarming, SmartSettingId.MaterialTravel),
            new(SmartSettingId.IdleSoulMap, materials, SmartSettingsText.IdleSoulMap, c.IdleBestSoulsMapTravel, SmartSettingId.MaterialTravel),
            new(SmartSettingId.MaterialQuestCapacity, materials, SmartSettingsText.MaterialQuestCapacity, c.MaterialQuestCapacityReliefEnabled, SmartSettingId.QuestAutomation),
            new(SmartSettingId.OpportunisticMaterials, materials, SmartSettingsText.OpportunisticMaterials, c.OpportunisticTaskMapMaterialsEnabled, SmartSettingId.QuestAutomation),

            new(SmartSettingId.PermanentMultipliers, endgame, SmartSettingsText.PermanentMultipliers, c.PermanentFreeMultipliersEnabled, SmartSettingId.EndgameMode),
            new(SmartSettingId.PermanentSouls, endgame, SmartSettingsText.PermanentSouls, c.PermanentFreeSoulsBonusEnabled, SmartSettingId.EndgameMode, SmartSettingId.PermanentMultipliers),
            new(SmartSettingId.PermanentCraftables, endgame, SmartSettingsText.PermanentCraftables, c.PermanentTimedCraftableEffectsEnabled, SmartSettingId.EndgameMode),
            new(SmartSettingId.PermanentPaid500, endgame, SmartSettingsText.PermanentPaid500, c.EndgamePermanentPaidBonusesEnabled, SmartSettingId.EndgameMode)
        };
    }
}

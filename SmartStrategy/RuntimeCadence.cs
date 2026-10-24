namespace SmartStrategy;

/// <summary>
/// Fixed coordination delays used to let native Unity and quest state settle.
/// Player-tunable strategy thresholds remain in SmartConfig instead.
/// </summary>
internal static class RuntimeCadence
{
    internal const float SettingsInitialMountDelaySeconds = 0.5f;
    internal const float SettingsMountRetrySeconds = 2f;
    internal const float SettingsMountFailureRetrySeconds = 10f;
    internal const float SettingsPopupCloseGraceSeconds = 1f;

    internal const float QuestActiveTimeSampleSeconds = 0.5f;
    internal const float QuestCompatibilityRetrySeconds = 30f;
    internal const float QuestMutationSettleSeconds = 1.5f;
    internal const float QuestGenerationSettleSeconds = 5f;
    internal const float QuestGenerationFailureRetrySeconds = 15f;
    internal const float QuestRerollAvailabilityRetrySeconds = 1f;
    internal const float QuestReplacementSettleTimeoutSeconds = 8f;
    internal const float QuestRerollPermissionLogSeconds = 10f;

    internal const float ChestPreparationLatchSeconds = 2f;
    internal const float PortalGroundReservationSeconds = 0.75f;
    internal const float CraftActionBackoffSeconds = 1.5f;
    internal const float CraftFailureBackoffSeconds = 30f;
    internal const float CraftSkipLogSeconds = 30f;
    internal const float SupplementalRefineCheckSeconds = 3f;
}

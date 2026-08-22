using HarmonyLib;
using Il2Cpp;

namespace SmartStrategy;

[HarmonyPatch(typeof(ChestHuntManager), nameof(ChestHuntManager.StartEvent))]
internal static class ChestHuntStartPatch
{
    private static void Prefix()
    {
        try
        {
            StrategyBehaviour.Current?.PrepareForChestHunt("ChestHuntManager.StartEvent prefix");
            StrategyBehaviour.Current?.PreparePaidSoulsPurchaseForChestHunt();
        }
        catch (Exception ex) { SmartLog.Warning(SmartLogCategory.Event, $"Chest Hunt preparation failed; the original game flow was allowed: {ex.GetType().Name}: {ex.Message}"); }
    }
}

// The normal key path creates this coroutine directly from ChestHuntKey.OnTriggerStay2D instead of
// calling ChestHuntManager.StartEvent. Run preparation only on the first MoveNext (state=0), avoiding
// repeated evaluation after a yield or a purchase after the minigame has already started.
[HarmonyPatch(typeof(ChestHuntManager._StartEventDelayed_d__66), "MoveNext")]
internal static class ChestHuntDelayedStartPatch
{
    private static void Prefix(ChestHuntManager._StartEventDelayed_d__66 __instance)
    {
        if (__instance == null || __instance.__1__state != 0) return;
        try
        {
            StrategyBehaviour.Current?.PrepareForChestHunt("native Chest Hunt coroutine prefix");
            StrategyBehaviour.Current?.PreparePaidSoulsPurchaseForChestHunt();
        }
        catch (Exception ex)
        {
            SmartLog.Warning(SmartLogCategory.Event,
                $"Chest Hunt coroutine preparation failed; the original game flow was allowed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(ChestHuntKey), nameof(ChestHuntKey.OnTriggerEnter2D))]
internal static class ChestHuntKeyTriggerPatch
{
    private static void Prefix()
    {
        try
        {
// Paid multipliers and Spyglass or Compass share the same requested pre-trigger timing. The two
// preparation chains remain independent: missing ordinary Souls items or reserves cannot block a ready purchase.
            StrategyBehaviour.Current?.PrepareForChestHunt("Chest Hunt key warning range");
            StrategyBehaviour.Current?.PreparePaidSoulsPurchaseForChestHunt();
        }
        catch (Exception ex) { SmartLog.Warning(SmartLogCategory.Event, $"Chest Hunt key pre-trigger preparation failed; the original game flow was allowed: {ex.GetType().Name}: {ex.Message}"); }
    }
}

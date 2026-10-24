using HarmonyLib;
using Il2Cpp;

namespace SmartStrategy;

[HarmonyPatch(typeof(RageModeManager), nameof(RageModeManager.Activate))]
internal static class RageMovementGuard
{
    private static void Prefix(RageModeManager __instance)
    {
        if (__instance == null || __instance.currentCd > 0.01) return;
        // Also covers native/manual activation after the automation Update has already started a
        // jump in this frame. The following frames guard Preparing, Execution and Ending states.
        try { StrategyBehaviour.Current?.YieldMovementToNativeRage(); }
        catch (Exception ex)
        {
            SmartLog.Warning(SmartLogCategory.Combat,
                $"Unable to release automatic movement before Rage: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

using HarmonyLib;
using Il2Cpp;

namespace SmartStrategy;

/// <summary>
/// Captures the exact range argument used by the game's own battle-mode attack. Synthetic calls are
/// excluded from capture so a guessed value can never overwrite an observed native value.
/// </summary>
[HarmonyPatch(typeof(BattleModeManager), nameof(BattleModeManager.BMAttack))]
internal static class BattleModeAttackBridge
{
    internal static bool SyntheticCall { get; set; }
    internal static bool HasObservedRange { get; private set; }
    internal static float ObservedRangeX { get; private set; }

    private static void Prefix(float rangeX)
    {
        if (SyntheticCall || !float.IsFinite(rangeX) || rangeX < 0) return;
        ObservedRangeX = rangeX;
        HasObservedRange = true;
    }

    internal static void ResetEncounter()
    {
        SyntheticCall = false;
        // rangeX is a native attack parameter for the current game version and session, not one Giant. Reuse it
        // after first capture; process exit clears the static state and the next run calibrates again.
    }
}

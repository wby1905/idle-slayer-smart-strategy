using System.Reflection;
using HarmonyLib;

namespace SmartStrategy;

internal static class AutoProgressionAscensionGuard
{
    private static bool _installed;
    private static bool _blockLogged;
    private static FieldInfo _waitingForPostAscensionField;

    internal static void TryInstall(HarmonyLib.Harmony harmony)
    {
        if (_installed || harmony == null) return;

        try
        {
            var serviceType = AccessTools.TypeByName(AutoProgressionContract.AscensionServiceTypeName);
            var tick = serviceType == null
                ? null
                : AccessTools.Method(serviceType, AutoProgressionContract.AscensionTickMethodName,
                    new[] { typeof(float), typeof(bool) });
            var prefix = AccessTools.Method(typeof(AutoProgressionAscensionGuard), nameof(Prefix));
            if (serviceType == null || tick == null || prefix == null)
            {
                SmartLog.Debug(SmartLogCategory.Compatibility,
                    "AutoProgression's automatic Ascension entry point is not available yet; initialization will retry later.");
                return;
            }

            _waitingForPostAscensionField = AccessTools.Field(serviceType,
                AutoProgressionContract.WaitingForPostAscensionFieldName);
            harmony.Patch(tick, prefix: new HarmonyMethod(prefix));
            _installed = true;
            SmartLog.Debug(SmartLogCategory.Compatibility,
                "AutoProgression automatic Ascension protection is ready.");
        }
        catch (Exception ex)
        {
            SmartLog.Warning(SmartLogCategory.Compatibility,
                $"Unable to install AutoProgression Ascension protection; its behavior was not changed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool Prefix(object __instance, ref bool __result)
    {
        var behaviour = StrategyBehaviour.Current;
        if (behaviour?.BlocksAutomaticNormalAscension != true)
        {
            _blockLogged = false;
            return true;
        }

        // If AutoProgression already started an Ascension, allow its trailing Buy All phase to finish.
        // This guard prevents only a new Ascension that has not started yet.
        try
        {
            if (_waitingForPostAscensionField?.GetValue(__instance) is true)
                return true;
        }
        catch
        {
            // If the trailing phase cannot be confirmed, protect the value window and block a new Tick.
        }

        __result = false;
        if (!_blockLogged)
        {
            _blockLogged = true;
            SmartLog.Info(SmartLogCategory.Compatibility,
                "Skipped one AutoProgression Ascension during the paid Souls window; normal behavior resumes when the bonus ends.");
        }
        return false;
    }
}

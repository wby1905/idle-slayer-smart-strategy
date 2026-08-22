using IdleSlayerMods.Common;
using Il2Cpp;
using UnityEngine;

namespace SmartStrategy;

internal static class RuntimeCompatibility
{
    internal static void LogStartupSnapshot()
    {
        SmartLog.Info(SmartLogCategory.Compatibility,
            $"Runtime snapshot: gameVersion={ReadGameVersion()}, gameAssembly={AssemblyVersion(typeof(Quest))}, " +
            $"commonAssembly={AssemblyVersion(typeof(ModHelper))}, runtime={Environment.Version}, " +
            $"configurationSchema={SmartConfig.CurrentConfigurationVersion}, gameContractRevision={GameContracts.Revision}.");
    }

    private static string ReadGameVersion()
    {
        try { return string.IsNullOrWhiteSpace(Application.version) ? "unknown" : Application.version; }
        catch { return "unavailable"; }
    }

    private static string AssemblyVersion(Type type)
    {
        try { return type.Assembly.GetName().Version?.ToString() ?? "unknown"; }
        catch { return "unavailable"; }
    }
}

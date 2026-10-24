using IdleSlayerMods.Common;
using Il2Cpp;
using UnityEngine;
using HarmonyLib;

namespace SmartStrategy;

internal static class RuntimeCompatibility
{
    internal static void InstallPatches(HarmonyLib.Harmony harmony)
    {
        var installed = 0;
        foreach (var type in typeof(Plugin).Assembly.GetTypes()
                     .Where(type => Attribute.IsDefined(type, typeof(HarmonyPatch))))
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
                installed++;
            }
            catch (Exception ex)
            {
                SmartLog.Warning(SmartLogCategory.Compatibility,
                    $"Optional native hook '{type.Name}' could not be installed; other hooks will continue: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }
        SmartLog.Info(SmartLogCategory.Compatibility, $"Installed {installed} native hook groups.");
    }

    internal static void LogStartupSnapshot()
    {
        SmartLog.Info(SmartLogCategory.Compatibility,
            $"Runtime snapshot: gameVersion={ReadGameVersion()}, gameAssembly={AssemblyVersion(typeof(Quest))}, " +
            $"commonAssembly={AssemblyVersion(typeof(ModHelper))}, runtime={Environment.Version}, " +
            $"configurationSchema={SmartConfig.CurrentConfigurationVersion}, gameContractRevision={GameContracts.Revision}, " +
            $"gameAssemblyMvid={typeof(Quest).Module.ModuleVersionId}, questApi=data-object.");
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

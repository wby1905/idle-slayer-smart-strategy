using IdleSlayerMods.Common;
using MelonLoader;

[assembly: MelonInfo(typeof(SmartStrategy.Plugin), SmartStrategy.SmartStrategyInfo.PluginName,
    SmartStrategy.SmartStrategyInfo.PluginVersion, SmartStrategy.SmartStrategyInfo.PluginAuthor)]
[assembly: MelonAdditionalDependencies("IdleSlayerMods.Common")]

namespace SmartStrategy;

public sealed class Plugin : MelonMod
{
    internal static readonly MelonLogger.Instance Log = Melon<Plugin>.Logger;
    internal static SmartConfig Config;
    internal static ModHelper ModHelperInstance;
    internal static bool IsApplicationQuitting { get; private set; }
    internal static bool EndgameModeActive => Config?.EndgameModeEnabled.Value == true;

    public override void OnInitializeMelon()
    {
        IsApplicationQuitting = false;
        ModHelper.ModHelperMounted += instance => ModHelperInstance = instance;
        Config = new SmartConfig();
        HarmonyInstance.PatchAll();
        AutoProgressionAscensionGuard.TryInstall(HarmonyInstance);
        SmartLog.Info(SmartLogCategory.Core,
            $"{SmartStrategyInfo.PluginName} v{SmartStrategyInfo.PluginVersion} loaded. " +
            "Open the in-game Smart Strategy panel to configure automation.");
        RuntimeCompatibility.LogStartupSnapshot();
        SmartLog.Debug(SmartLogCategory.Core,
            "Debug logging is enabled; detailed planning, calibration, and compatibility diagnostics will be shown.");
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        if (sceneName != GameContracts.GameplaySceneName) return;
        SmartSettingsUi.OnGameSceneLoaded();
        if (StrategyBehaviour.Current == null)
            ModUtils.RegisterComponent<StrategyBehaviour>();
    }

    public override void OnLateInitializeMelon()
    {
        AutoProgressionAscensionGuard.TryInstall(HarmonyInstance);
    }

    public override void OnApplicationQuit()
    {
        SuspendForApplicationQuit();
    }

    public override void OnDeinitializeMelon()
    {
        SuspendForApplicationQuit();
    }

    internal static void SuspendForApplicationQuit()
    {
        IsApplicationQuitting = true;
        StrategyBehaviour.Current?.SuspendForGameQuit();
    }
}

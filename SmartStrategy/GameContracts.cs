using Il2Cpp;
using UnityEngine;

namespace SmartStrategy;

/// <summary>
/// Centralizes names and hierarchy paths owned by Idle Slayer or other mods.
/// When a game update changes a Unity hierarchy, compatibility work should begin here.
/// </summary>
internal static class GameContracts
{
    internal const int Revision = 2;
    internal const string GameplaySceneName = "Game";
    private const string InfoPanelButtonsPath =
        "UIManager/Safe Zone/Shop Panel/Wrapper/Info Panel/Info Panel/Viewport/Content/Buttons";
    private const string OptionsContentPath =
        "UIManager/Popup Options/Overlay/Panel/Scroll View/Viewport/Content";
    private const string PopupPath = "UIManager/Popup";

    private const string PanelButtonContentPath = "Background/Content";
    private const string SettingsSectionTemplateName = "Screen Title";
    private const string SettingsToggleTemplateName = "Confirm Portal";
    private const string SettingsLabelName = "Label";
    private const string SettingsAnimatedSwitchName = "Animated Switch";
    private const string NativeSettingsButtonName = "Settings";

    private const string ChestHuntKeyObjectName = "Chest Hunt Key(Clone)";
    private const string SpecialRandomBoxObjectName = "Special Random Box(Clone)";

    internal static GameObject FindInfoPanelButton(string objectName) =>
        FindObject($"{InfoPanelButtonsPath}/{objectName}");

    internal static GameObject FindOptionsContent() => FindObject(OptionsContentPath);

    internal static GameObject FindPopup() => FindObject(PopupPath);

    internal static GameObject FindSettingsSectionTemplate(GameObject optionsContent) =>
        FindChildObject(optionsContent, SettingsSectionTemplateName);

    internal static GameObject FindSettingsToggleTemplate(GameObject optionsContent) =>
        FindChildObject(optionsContent, SettingsToggleTemplateName);

    internal static Transform FindPanelButtonContent(GameObject panelButton) =>
        panelButton?.transform.Find(PanelButtonContentPath);

    internal static GameObject FindNativeSettingsButton() => FindInfoPanelButton(NativeSettingsButtonName);

    internal static Transform FindSettingsLabel(GameObject row) =>
        row?.transform.Find(SettingsLabelName);

    internal static Transform FindSettingsAnimatedSwitch(GameObject row) =>
        row?.transform.Find(SettingsAnimatedSwitchName);

    internal static bool IsChestHuntKeyPresent() => FindObject(ChestHuntKeyObjectName) != null;

    internal static bool IsSpecialRandomBoxPresent() => FindObject(SpecialRandomBoxObjectName) != null;

    internal static bool IsGameQuitRequested()
    {
        try { return UIManager.instance?.gameQuit == true; }
        catch { return false; }
    }

    private static GameObject FindChildObject(GameObject parent, string childPath) =>
        parent?.transform.Find(childPath)?.gameObject;

    private static GameObject FindObject(string path)
    {
        try { return GameObject.Find(path); }
        catch { return null; }
    }
}

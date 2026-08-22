using Il2Cpp;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SmartStrategy;

internal static partial class SmartSettingsUi
{
    private const string PanelButtonObjectName = "Smart Strategy";

    private static readonly Dictionary<SmartSettingId, bool> Draft = new();
    private static readonly Dictionary<SmartSettingId, Toggle> Controls = new();
    private static GameObject _content;
    private static GameObject _panelButton;
    private static Popup _popup;
    private static bool _buttonMounted;
    private static SmartSettingsLanguage? _buttonLanguage;
    private static float _nextMountAttemptAt;
    private static float _openedAt;

    internal static bool IsOpen { get; private set; }

    internal static void OnGameSceneLoaded()
    {
        CleanupContent();
        _panelButton = null;
        _buttonMounted = false;
        _buttonLanguage = null;
        _nextMountAttemptAt = Time.unscaledTime + RuntimeCadence.SettingsInitialMountDelaySeconds;
    }

    internal static void Tick()
    {
        TryMountButton();
        RefreshPanelButtonLanguage();
        if (!IsOpen || Time.unscaledTime < _openedAt + RuntimeCadence.SettingsPopupCloseGraceSeconds) return;
        try
        {
            if (_popup == null || !_popup.IsVisible()) CleanupContent();
        }
        catch
        {
            CleanupContent();
        }
    }

    private static void TryMountButton()
    {
        if (_buttonMounted || Time.unscaledTime < _nextMountAttemptAt) return;
        _nextMountAttemptAt = Time.unscaledTime + RuntimeCadence.SettingsMountRetrySeconds;

        try
        {
            var existingButton = FindPanelButton();
            if (existingButton != null)
            {
                BindPanelButton(existingButton);
                return;
            }

            var buttonLabel = SmartSettingsLocalization.Text(SmartSettingsText.PanelButton);
            existingButton = CloneNativeSettingsButton(buttonLabel);
            if (existingButton != null)
            {
                BindPanelButton(existingButton);
                LogButtonMounted();
                return;
            }

            var helper = Plugin.ModHelperInstance;
            if (helper == null) return;
#pragma warning disable CS0618
            helper.AddPanelButton(buttonLabel, Open, SmartSettingsIcon.GetOrCreate());
#pragma warning restore CS0618
            existingButton = GameContracts.FindInfoPanelButton(buttonLabel);
            if (existingButton != null) BindPanelButton(existingButton);
            LogButtonMounted();
        }
        catch (Exception ex)
        {
            _nextMountAttemptAt = Time.unscaledTime + RuntimeCadence.SettingsMountFailureRetrySeconds;
            SmartLog.Warning(SmartLogCategory.Settings,
                $"Unable to mount the Smart Strategy settings button; retrying later: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static GameObject CloneNativeSettingsButton(string label)
    {
        GameObject clone = null;
        try
        {
            var template = GameContracts.FindNativeSettingsButton();
            if (template == null || template.transform.parent == null) return null;

            clone = UnityEngine.Object.Instantiate(template, template.transform.parent);
            clone.name = PanelButtonObjectName;

            var content = GameContracts.FindPanelButtonContent(clone);
            if (content == null || content.childCount < 2)
                throw new InvalidOperationException("The native Settings button content is incomplete.");

            var startingText = content.GetChild(1).GetComponent<StartingText>();
            if (startingText != null) UnityEngine.Object.DestroyImmediate(startingText);
            LocalizedUiText.Set(content.GetChild(1), label);

            var button = clone.GetComponent<Button>();
            if (button == null) throw new InvalidOperationException("The native Settings template has no Button.");
            ((UnityEventBase)button.onClick).RemoveAllListeners();
            ((UnityEvent)(object)button.onClick).AddListener((UnityAction)(Action)Open);
            clone.SetActive(true);
            return clone;
        }
        catch (Exception ex)
        {
            try
            {
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
            }
            catch { /* The incomplete clone may already be destroyed. */ }
            SmartLog.Warning(SmartLogCategory.Settings,
                $"Unable to clone the native Settings button; using the compatibility fallback: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static void LogButtonMounted()
    {
        if (!_buttonMounted) return;
        SmartLog.Debug(SmartLogCategory.Settings,
            "The in-game settings entry is mounted; switches edit a draft until Apply is pressed.");
    }

    private static GameObject FindPanelButton()
    {
        var button = GameContracts.FindInfoPanelButton(PanelButtonObjectName);
        if (button != null) return button;

        button = GameContracts.FindInfoPanelButton(
            SmartSettingsLocalization.Text(SmartSettingsText.PanelButton));
        if (button != null) return button;

        button = GameContracts.FindInfoPanelButton(
            SmartSettingsLocalization.Text(
                SmartSettingsLanguage.SimplifiedChinese,
                SmartSettingsText.PanelButton));
        return button ?? GameContracts.FindInfoPanelButton(
            SmartSettingsLocalization.Text(
                SmartSettingsLanguage.English,
                SmartSettingsText.PanelButton));
    }

    private static void BindPanelButton(GameObject button)
    {
        _panelButton = button;
        _panelButton.name = PanelButtonObjectName;
        _buttonMounted = true;
        _buttonLanguage = null;
        RefreshPanelButtonLanguage();
    }

    private static void RefreshPanelButtonLanguage()
    {
        if (!_buttonMounted) return;
        if (_panelButton == null)
        {
            _buttonMounted = false;
            _buttonLanguage = null;
            return;
        }

        var language = SmartSettingsLocalization.CurrentLanguage;
        if (_buttonLanguage == language) return;

        var content = GameContracts.FindPanelButtonContent(_panelButton);
        if (content == null || content.childCount < 2) return;
        LocalizedUiText.Set(
            content.GetChild(1),
            SmartSettingsLocalization.Text(language, SmartSettingsText.PanelButton));
        _buttonLanguage = language;
    }

    private static void Open()
    {
        if (IsOpen) return;
        try
        {
            _popup = GameContracts.FindPopup()?.GetComponent<Popup>();
            if (_popup == null)
            {
                Plugin.ModHelperInstance?.ShowNotification(
                    SmartSettingsLocalization.Text(SmartSettingsText.UiNotReady), false);
                return;
            }
            if (_popup.IsVisible())
            {
                Plugin.ModHelperInstance?.ShowNotification(
                    SmartSettingsLocalization.Text(SmartSettingsText.CloseCurrentPopup), false);
                return;
            }

            var contentTemplate = GameContracts.FindOptionsContent();
            var sectionTemplate = GameContracts.FindSettingsSectionTemplate(contentTemplate);
            var toggleTemplate = GameContracts.FindSettingsToggleTemplate(contentTemplate);
            if (contentTemplate == null || sectionTemplate == null || toggleTemplate == null)
                throw new InvalidOperationException("The native settings template is not loaded.");

            BuildDraft();
            _content = UnityEngine.Object.Instantiate(contentTemplate);
            _content.name = "Smart Strategy Settings Content";
            for (var index = _content.transform.childCount - 1; index >= 0; index--)
                UnityEngine.Object.DestroyImmediate(_content.transform.GetChild(index).gameObject);

            SmartSettingsText? lastSection = null;
            foreach (var definition in Definitions())
            {
                if (lastSection != definition.Section)
                {
                    AddSection(sectionTemplate, definition.Section);
                    lastSection = definition.Section;
                }
                AddToggle(toggleTemplate, definition);
            }
            UpdateDependencies();
            _content.SetActive(true);
            IsOpen = true;
            _openedAt = Time.unscaledTime;

            Plugin.ModHelperInstance.ShowDialog(
                title: SmartSettingsLocalization.Text(SmartSettingsText.Title),
                subtitle: SmartSettingsLocalization.Text(SmartSettingsText.Subtitle),
                descriptionText: SmartSettingsLocalization.Text(SmartSettingsText.EndgameRiskDescription),
                descriptionColor: new Color(1f, 0.67f, 0.2f, 1f),
                centerDescription: false,
                confirmText: SmartSettingsLocalization.Text(SmartSettingsText.Apply),
                confirmAction: Apply,
                cancelText: SmartSettingsLocalization.Text(SmartSettingsText.Cancel),
                cancelAction: Cancel,
                content: _content,
                overridePopup: false);

            _content.transform.localScale = Vector3.one;
            _content.transform.localPosition = Vector3.zero;
        }
        catch (Exception ex)
        {
            CleanupContent();
            SmartLog.Warning(SmartLogCategory.Settings,
                $"Unable to open Smart Strategy settings: {ex.GetType().Name}: {ex.Message}");
            Plugin.ModHelperInstance?.ShowNotification(
                SmartSettingsLocalization.Text(SmartSettingsText.OpenFailed), false);
        }
    }

    private static void BuildDraft()
    {
        Draft.Clear();
        Controls.Clear();
        foreach (var definition in Definitions()) Draft[definition.Key] = definition.Entry.Value;
    }

    private static void AddSection(GameObject template, SmartSettingsText text)
    {
        var label = SmartSettingsLocalization.Text(text);
        var row = UnityEngine.Object.Instantiate(template, _content.transform);
        row.name = $"Section - {label}";
        row.SetActive(true);
        var labelTransform = GameContracts.FindSettingsLabel(row);
        if (labelTransform != null) LocalizedUiText.Set(labelTransform, label);
    }

    private static void AddToggle(GameObject template, Definition definition)
    {
        var row = UnityEngine.Object.Instantiate(template, _content.transform);
        row.name = $"Setting - {definition.Key}";
        row.SetActive(true);
        var labelTransform = GameContracts.FindSettingsLabel(row);
        if (labelTransform != null)
            LocalizedUiText.Set(labelTransform, SmartSettingsLocalization.Text(definition.Label));

        var switchTransform = GameContracts.FindSettingsAnimatedSwitch(row);
        var toggle = switchTransform?.GetComponent<Toggle>();
        if (toggle == null) throw new InvalidOperationException($"The settings template has no Toggle for {definition.Key}.");
        ((UnityEventBase)toggle.onValueChanged).RemoveAllListeners();
        toggle.isOn = Draft[definition.Key];
        ((UnityEvent<bool>)(object)toggle.onValueChanged).AddListener(
            (UnityAction<bool>)(Action<bool>)(state =>
            {
                Draft[definition.Key] = state;
                UpdateDependencies();
            }));
        Controls[definition.Key] = toggle;
    }

    private static void UpdateDependencies()
    {
        foreach (var definition in Definitions())
        {
            if (!Controls.TryGetValue(definition.Key, out var toggle) || toggle == null) continue;
            var available = true;
            foreach (var requirement in definition.Requires)
            {
                if (!Draft.TryGetValue(requirement, out var enabled) || !enabled)
                {
                    available = false;
                    break;
                }
            }
            toggle.interactable = available;
        }
    }

    private static void Apply()
    {
        var originals = new Dictionary<SmartSettingId, bool>();
        var changed = new List<SmartSettingId>();
        try
        {
            foreach (var definition in Definitions())
            {
                originals[definition.Key] = definition.Entry.Value;
                var next = Draft[definition.Key];
                if (definition.Entry.Value == next) continue;
                definition.Entry.Value = next;
                changed.Add(definition.Key);
            }

            MelonPreferences.Save();
            StrategyBehaviour.Current?.OnSettingsApplied(changed);
            var endgameEnabled = changed.Contains(SmartSettingId.EndgameMode) &&
                                 Plugin.Config.EndgameModeEnabled.Value;
            CloseDialog();
            Plugin.ModHelperInstance?.ShowNotification(
                endgameEnabled
                    ? SmartSettingsLocalization.Text(SmartSettingsText.EndgameEnabledWarning)
                    : changed.Count == 0
                    ? SmartSettingsLocalization.Text(SmartSettingsText.NoChanges)
                    : SmartSettingsLocalization.Format(SmartSettingsText.ChangesApplied, changed.Count),
                !endgameEnabled);
        }
        catch (Exception ex)
        {
            foreach (var definition in Definitions())
                if (originals.TryGetValue(definition.Key, out var original)) definition.Entry.Value = original;
            try { MelonPreferences.Save(); } catch { /* Preserve the original exception. */ }
            SmartLog.Warning(SmartLogCategory.Settings,
                $"Unable to apply Smart Strategy settings; the draft was rolled back: {ex.GetType().Name}: {ex.Message}");
            CloseDialog();
            Plugin.ModHelperInstance?.ShowNotification(
                SmartSettingsLocalization.Text(SmartSettingsText.ApplyFailed), false);
        }
    }

    private static void Cancel() => CloseDialog();

    private static void CloseDialog()
    {
        var popup = _popup;
        try
        {
            if (popup != null && popup.IsVisible()) popup.Close();
        }
        catch (Exception ex)
        {
            SmartLog.Warning(SmartLogCategory.Settings,
                $"Unable to close the Smart Strategy settings popup cleanly: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            CleanupContent();
        }
    }

    private static void CleanupContent()
    {
        IsOpen = false;
        Draft.Clear();
        Controls.Clear();
        try
        {
            if (_content != null) UnityEngine.Object.Destroy(_content);
        }
        catch { /* The object may already be destroyed during scene unload. */ }
        _content = null;
        _popup = null;
    }

}

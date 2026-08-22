using IdleSlayerMods.Common.Helpers;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;

namespace SmartStrategy;

internal static class LocalizedUiText
{
    internal static bool Set(Transform label, string text)
    {
        if (label == null) return false;

        UIHelper.SetLabelText(label, text ?? string.Empty);
        var textComponent = label.GetComponent<TextMeshProUGUI>();
        if (textComponent == null) return false;

        try
        {
            var language = LocalizationSystem.currentLanguage;
            if (language?.fontAsset != null) textComponent.font = language.fontAsset;
            if (language?.outlineMaterial != null)
                textComponent.fontSharedMaterial = language.outlineMaterial;
            textComponent.isRightToLeftText = language?.isRTL == true;
            textComponent.text = text ?? string.Empty;
            textComponent.ForceMeshUpdate();
        }
        catch (Exception ex)
        {
            SmartLog.Debug(SmartLogCategory.Settings,
                $"Unable to apply the current game-language font; retaining the cloned font: {ex.GetType().Name}: {ex.Message}");
        }

        return true;
    }
}

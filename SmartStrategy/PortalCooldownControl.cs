using Il2Cpp;
using UnityEngine;

namespace SmartStrategy;

public sealed partial class StrategyBehaviour
{
    private float _nextPortalCooldownResetErrorLogAt;
    private bool _portalCooldownResetObserved;

    private void MaintainPortalCooldownReset()
    {
        if (!PortalCooldownResetActive()) return;
        try
        {
            var portal = PortalButton.instance;
            if (portal == null || portal.currentCd <= 0) return;
            portal.currentCd = 0;
            if (_portalCooldownResetObserved) return;
            _portalCooldownResetObserved = true;
            SmartLog.Debug(SmartLogCategory.Portal,
                "Smart Strategy now owns the native Portal cooldown reset; route estimates use the zero-cooldown model.");
        }
        catch (Exception ex)
        {
            if (Time.unscaledTime < _nextPortalCooldownResetErrorLogAt) return;
            _nextPortalCooldownResetErrorLogAt = Time.unscaledTime + 30f;
            SmartLog.Warning(SmartLogCategory.Portal,
                $"Portal cooldown reset failed; the operation will retry later: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool PortalCooldownResetActive() =>
        Plugin.Config.Enabled.Value && !_emergencyStopped && Plugin.Config.ResetPortalCooldown.Value;
}

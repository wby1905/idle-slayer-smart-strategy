using Il2Cpp;

namespace SmartStrategy;

internal static class PortalTimingEstimator
{
    private const double ConservativeNormalTransitionSeconds = 45;

    internal static bool ZeroCooldownActive =>
        Plugin.Config?.Enabled.Value == true && Plugin.Config.ResetPortalCooldown.Value;

    internal static double FirstTransitionSeconds()
    {
        if (ZeroCooldownActive) return ZeroCooldownTransitionSeconds();
        try { return Math.Max(ConservativeNormalTransitionSeconds, PortalButton.instance?.currentCd ?? 0); }
        catch { return ConservativeNormalTransitionSeconds; }
    }

    internal static double AdditionalTransitionSeconds()
    {
        if (ZeroCooldownActive) return ZeroCooldownTransitionSeconds();
        try { return Math.Max(ConservativeNormalTransitionSeconds, PortalButton.instance?.GetCD() ?? 0); }
        catch { return ConservativeNormalTransitionSeconds; }
    }

    internal static double PortalQuestSeconds(double remaining)
    {
        if (remaining <= 0) return 0;
        if (ZeroCooldownActive) return remaining * ZeroCooldownTransitionSeconds();
        try
        {
            var portal = PortalButton.instance;
            var currentCooldown = Math.Max(0, portal?.currentCd ?? 0);
            var fullCooldown = Math.Max(10, portal?.GetCD() ?? 1800);
            return currentCooldown + Math.Max(0, remaining - 1) * fullCooldown + remaining * 10;
        }
        catch { return remaining * 1800.0; }
    }

    private static double ZeroCooldownTransitionSeconds() =>
        Math.Max(2, Plugin.Config.ZeroCooldownPortalTransitionSeconds.Value);
}

using Il2Cpp;
using MelonLoader.Utils;
using System.Reflection;
using System.Security.Cryptography;

namespace SmartStrategy;

internal static class PaidSoulsPurchaseCompatibility
{
    // Verified from native instructions in Steam build 26139124 / Idle Slayer 7.3.6:
    // 0x48DFF0 reads button.cost, calls Spend at 0x48E1A2, activates PaidSoulsBonus,
    // saves, then records analytics. Spend checks balance before subtracting it.
    // Do not reuse this generated callback by name alone after a game update.
    private const string VerifiedGameSha256 = "E310FE2F86F5C74C54E58F78C04FADD297FF4DF093AE5B21A4227D55958595B7";
    private static bool _checked;
    private static bool _reported;
    private static bool _awaitingReconciliation;
    private static double _balanceBefore;
    private static double _expectedCost;
    private static int _expectedMultiplier;
    private static MethodInfo _callback;
    private static string _unavailableReason;

    internal static bool Available
    {
        get
        {
            ResolveVerifiedCallback();
            return _callback != null && !_awaitingReconciliation;
        }
    }

    internal static bool CheckAvailability()
    {
        if (Available) return true;
        if (!_reported)
        {
            _reported = true;
            SmartLog.Warning(SmartLogCategory.Compatibility,
                $"Automatic Paid Souls purchases are unavailable: {_unavailableReason ?? "the previous charge is unresolved"}. " +
                "Manual paid bonuses can still be adopted; no automatic purchase preparation will start.");
        }
        return false;
    }

    internal static string Status => Available ? "verified native purchase" :
        _awaitingReconciliation ? "purchase locked: awaiting exact charge/effect reconciliation" :
        _unavailableReason ?? "native purchase unavailable";

    internal static bool HasUnresolvedPurchase => _awaitingReconciliation;

    internal static void RequestPurchase(JewelsOfSoulSoulsBonus purchase, double expectedCost)
    {
        if (!Available) throw new NotSupportedException(_unavailableReason ?? "An earlier purchase has not been reconciled.");
        if (Plugin.IsApplicationQuitting || Plugin.Config?.Enabled.Value != true ||
            !Plugin.Config.PaidSoulsChestBurstEnabled.Value)
            throw new InvalidOperationException("Paid purchase automation is no longer enabled.");
        var inventory = PlayerInventory.instance;
        var manager = JewelsOfSoulManager.instance;
        var button = purchase?.button;
        if (inventory == null || manager?.paidSoulsBonus == null || button == null || purchase.multiplier <= 0)
            throw new InvalidOperationException("Native purchase or live effect is not ready.");
        if (manager.paidSoulsBonus.IsActive())
            throw new InvalidOperationException("A paid Souls effect is already active; do not overwrite it.");
        var cost = button.cost;
        var balance = inventory.jewelsOfSoul;
        var reserve = Math.Max(0, Plugin.Config.PaidSoulsJewelReserve.Value);
        if (!double.IsFinite(cost) || cost <= 0 || !double.IsFinite(expectedCost) ||
            Math.Abs(cost - expectedCost) > 0.0001 || !double.IsFinite(balance) || balance < cost + reserve)
            throw new InvalidOperationException("The live price or Jewel reserve changed before purchasing.");

        _balanceBefore = balance;
        _expectedCost = cost;
        _expectedMultiplier = purchase.multiplier;
        _awaitingReconciliation = true; // Set before invoking; a trailing UI/analytics exception may follow a charge.
        _reported = false;
        _callback.Invoke(purchase, null);
        ReconcileLiveEffect();
    }

    internal static void ReconcileLiveEffect()
    {
        if (!_awaitingReconciliation) return;
        try
        {
            var effect = JewelsOfSoulManager.instance?.paidSoulsBonus;
            var inventory = PlayerInventory.instance;
            if (effect == null || inventory == null || !effect.IsActive() ||
                effect.timeLeft <= 0 || effect.soulsMultiplier != _expectedMultiplier) return;
            var charged = _balanceBefore - inventory.jewelsOfSoul;
            if (!double.IsFinite(charged) || Math.Abs(charged - _expectedCost) > 0.0001) return;
            _awaitingReconciliation = false;
            _reported = false;
        }
        catch { /* Keep the purchase locked until both live observations are available. */ }
    }

    private static void ResolveVerifiedCallback()
    {
        if (_checked) return;
        _checked = true;
        try
        {
            var path = Path.Combine(MelonEnvironment.GameRootDirectory, "GameAssembly.dll");
            using var file = File.OpenRead(path);
            using var sha256 = SHA256.Create();
            var hash = Convert.ToHexString(sha256.ComputeHash(file));
            if (!string.Equals(hash, VerifiedGameSha256, StringComparison.Ordinal))
            {
                _unavailableReason = "this game binary has not had its native purchase flow verified";
                return;
            }
            var callback = typeof(JewelsOfSoulSoulsBonus).GetMethod("_PurchaseHandler_b__2_0",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                null, Type.EmptyTypes, null);
            if (callback == null || callback.ReturnType != typeof(void))
            {
                _unavailableReason = "the generated interop wrapper does not match the verified purchase callback";
                return;
            }
            _callback = callback;
            SmartLog.Info(SmartLogCategory.Compatibility,
                "Native Paid Souls purchase flow verified for Steam build 26139124; automatic purchases are available with live balance/effect reconciliation.");
        }
        catch (Exception ex)
        {
            _unavailableReason = $"unable to verify purchase compatibility ({ex.GetType().Name})";
        }
    }
}

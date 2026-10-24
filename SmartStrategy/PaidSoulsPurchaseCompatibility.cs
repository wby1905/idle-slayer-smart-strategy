using Il2Cpp;
using System.Reflection;

namespace SmartStrategy;

internal static class PaidSoulsPurchaseCompatibility
{
    // Verified from native instructions in Steam build 26139124 / Idle Slayer 7.3.6:
    // 0x48DFF0 reads button.cost, calls Spend at 0x48E1A2, activates PaidSoulsBonus,
    // saves, then records analytics. Spend checks balance before subtracting it.
    // Resolve the generated callback by signature across updates; there is no game-build allowlist.
    // Live price, charge and effect checks remain in the shared purchase coordinator.
    private static bool _checked;
    private static bool _reported;
    private static MethodInfo _callback;
    private static string _unavailableReason;

    internal static bool Available
    {
        get
        {
            ResolveCallback();
            return _callback != null && !JewelPurchaseCoordinator.HasPending;
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

    internal static string Status => Available ? "native purchase callback available" :
        JewelPurchaseCoordinator.HasPending ? "purchase locked: shared Jewel journal requires reconciliation" :
        _unavailableReason ?? "native purchase unavailable";

    internal static bool HasUnresolvedPurchase => JewelPurchaseCoordinator.HasPending;

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

        var multiplier = purchase.multiplier;
        JewelPurchaseObservation Observe()
        {
            var effect = JewelsOfSoulManager.instance?.paidSoulsBonus;
            if (effect == null || PlayerInventory.instance == null || purchase?.button == null)
                throw new InvalidOperationException("Paid Souls observation is unavailable.");
            return new JewelPurchaseObservation(PlayerInventory.instance.jewelsOfSoul, purchase.button.cost,
                $"{purchase.GetInstanceID()}|{purchase.multiplier}|{effect.GetInstanceID()}|{effect.IsActive()}|{effect.soulsMultiplier}|{effect.timeLeft:R}");
        }
        var result = JewelPurchaseCoordinator.Execute(JewelPurchaseKind.PaidSouls, cost,
            $"Paid Souls {multiplier}x", Observe, () => { _callback.Invoke(purchase, null); return true; },
            () => manager.paidSoulsBonus != null && manager.paidSoulsBonus.IsActive() &&
                  manager.paidSoulsBonus.timeLeft > 0 && manager.paidSoulsBonus.soulsMultiplier == multiplier,
            out var message);
        _reported = false;
        if (result == JewelPurchaseResult.Rejected) throw new InvalidOperationException(message);
        SmartLog.Info(SmartLogCategory.Endgame, message);
    }

    internal static void ReconcileLiveEffect()
    {
        JewelPurchaseCoordinator.ReconcileLivePurchase();
    }

    private static void ResolveCallback()
    {
        if (_checked) return;
        _checked = true;
        try
        {
            var callback = typeof(JewelsOfSoulSoulsBonus).GetMethod("_PurchaseHandler_b__2_0",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                null, Type.EmptyTypes, null);
            if (callback == null || callback.ReturnType != typeof(void))
            {
                _unavailableReason = "the native purchase callback is missing or has an unsupported signature";
                return;
            }
            _callback = callback;
            SmartLog.Info(SmartLogCategory.Compatibility,
                "Native Paid Souls purchase callback resolved; automatic purchases use live price, budget, balance and effect checks without a game-version restriction.");
        }
        catch (Exception ex)
        {
            _unavailableReason = $"unable to resolve the native purchase callback ({ex.GetType().Name})";
        }
    }
}

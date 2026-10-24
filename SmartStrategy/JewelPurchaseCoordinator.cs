using System.Globalization;

namespace SmartStrategy;

internal readonly record struct JewelPurchaseObservation(double Balance, double Price, string State);
internal enum JewelPurchaseResult { Rejected, Confirmed, Pending }

// One transaction gate and journal for every automatic Jewel consumer. Adapters own native
// eligibility and exact result checks; this layer owns money, persistence and uncertainty.
internal static class JewelPurchaseCoordinator
{
    private const double Epsilon = 0.0001;
    private static readonly object Gate = new();
    private static readonly JewelBudgetJournal Journal = new();
    private static JewelBudgetJournal.Ledger _ledger;
    private static string _haltReason;
    private static bool _executing;
    private static Func<JewelPurchaseObservation> _liveObservation;
    private static Func<bool> _liveConfirmation;
    private static string _liveToken;

    internal static bool HasPending
    {
        get { lock (Gate) { try { EnsureLoaded(); return _ledger.Pending != null || _haltReason != null; }
            catch (Exception ex) { Halt(ex); return true; } } }
    }

    internal static string Status
    {
        get
        {
            lock (Gate)
            {
                try
                {
                    EnsureLoaded();
                    var current = _ledger.Date == Today ? _ledger.Spent : 0;
                    return $"Jewels UTC {Today}: {current:0.##}/{Plugin.Config.GlobalDailyJewelBudget.Value}, " +
                        $"pending={_ledger.Pending?.Kind ?? "none"}" + (_haltReason == null ? "" : $"; blocked: {_haltReason}");
                }
                catch (Exception ex) { Halt(ex); return "Jewels: all automatic spending blocked; " + _haltReason; }
            }
        }
    }

    internal static bool CanAfford(JewelPurchaseKind kind, double price, double balance, out string reason)
    {
        lock (Gate)
        {
            try
            {
                EnsureLoaded();
                if (_executing || _haltReason != null || _ledger.Pending != null)
                { reason = "An automatic charge or budget journal is unresolved"; return false; }
                return CheckLimits(kind, price, balance, _ledger, out reason);
            }
            catch (Exception ex) { Halt(ex); reason = _haltReason; return false; }
        }
    }

    internal static JewelPurchaseResult Execute(JewelPurchaseKind kind, double expectedCost, string description,
        Func<JewelPurchaseObservation> observe, Func<bool> invoke, Func<bool> confirm, out string message,
        Func<bool> canInvoke = null)
    {
        lock (Gate)
        {
            message = "Automatic Jewel payment refused";
            if (_executing || observe == null || invoke == null || confirm == null)
                return JewelPurchaseResult.Rejected;
            _executing = true;
            var nativeInvoked = false;
            JewelBudgetJournal.Ledger previous = null;
            try
            {
                EnsureLoaded();
                if (_haltReason != null || _ledger.Pending != null)
                { message = "Automatic Jewel spending is locked by an unresolved charge/journal"; return JewelPurchaseResult.Rejected; }
                // Re-read persistent state at the transaction boundary, not on every game frame.
                _ledger = LoadJournal();
                if (_ledger.Pending != null) { message = "A persisted charge is unresolved"; return JewelPurchaseResult.Rejected; }
                if (canInvoke != null && !canInvoke())
                { message = "The requested native operation is no longer enabled or available"; return JewelPurchaseResult.Rejected; }
                var before = observe();
                if (!Equal(before.Price, expectedCost) || before.State == null ||
                    !CheckLimits(kind, expectedCost, before.Balance, _ledger, out message)) return JewelPurchaseResult.Rejected;
                previous = CurrentDay(_ledger);
                var reserved = previous.Copy();
                reserved.Spent += expectedCost;
                if (kind == JewelPurchaseKind.Refinery) reserved.RefinerySpent += expectedCost;
                else reserved.PaidSoulsSpent += expectedCost;
                reserved.Pending = new JewelBudgetJournal.Charge
                {
                    Token = Guid.NewGuid().ToString("N"), Kind = kind.ToString(), Cost = expectedCost,
                    BalanceBefore = before.Balance, StateBefore = before.State, Description = description
                };
                _ledger = reserved; // Set the in-memory barrier before any file operation.
                SaveJournal(reserved);
                _liveToken = reserved.Pending.Token;
                _liveObservation = observe;
                _liveConfirmation = confirm;
                if (Plugin.IsApplicationQuitting || !Enabled(kind) || canInvoke != null && !canInvoke())
                {
                    Complete(previous);
                    message = "Payment deferred: the requested operation became disabled before the native call";
                    return JewelPurchaseResult.Rejected;
                }
                var finalObservation = observe();
                // Use the pre-reservation ledger here: including our own reserved amount would
                // count this purchase twice. Re-read live settings after the durable write.
                if (!Same(before, finalObservation) ||
                    !CheckLimits(kind, expectedCost, finalObservation.Balance, previous, out message))
                {
                    Complete(previous);
                    message = "Payment deferred: live state or spending limits changed before the native call";
                    return JewelPurchaseResult.Rejected;
                }
                if (previous.Date != Today)
                {
                    Complete(previous);
                    message = "Payment deferred: the UTC budget day changed; re-evaluate on the next pass";
                    return JewelPurchaseResult.Rejected;
                }

                bool accepted;
                try { nativeInvoked = true; accepted = invoke(); }
                catch (Exception ex)
                {
                    // A callback can throw after charging. Confirm only by exact debit and result.
                    if (TryConfirm()) { message = "Jewel charge/result confirmed after a trailing native exception"; return JewelPurchaseResult.Confirmed; }
                    message = $"Uncertain {kind} charge ({ex.GetType().Name}); all automatic Jewel spending is blocked";
                    return JewelPurchaseResult.Pending;
                }
                if (TryConfirm())
                {
                    message = $"{description}: confirmed {expectedCost:0.##} Jewels; {Status}";
                    return JewelPurchaseResult.Confirmed;
                }
                if (!accepted && Same(before, observe()))
                {
                    Complete(previous);
                    message = "Native payment rejected without changing balance or state; reservation released";
                    return JewelPurchaseResult.Rejected;
                }
                message = "Charge/result pending; all automatic Jewel spending is blocked until reconciled";
                return JewelPurchaseResult.Pending;
            }
            catch (Exception ex)
            {
                // Persistence failures already set the permanent barrier in Load/SaveJournal.
                // A transient game-state read is different: before invocation it cannot have
                // charged anything, and after invocation the durable pending record is enough
                // to stop retries while a later live observation attempts confirmation.
                if (_haltReason != null)
                {
                    message = _haltReason;
                    return _ledger?.Pending == null ? JewelPurchaseResult.Rejected : JewelPurchaseResult.Pending;
                }
                if (!nativeInvoked)
                {
                    if (previous != null && _ledger?.Pending != null)
                    {
                        try { Complete(previous); }
                        catch
                        {
                            message = _haltReason ?? "Unable to release an uninvoked purchase reservation";
                            return JewelPurchaseResult.Pending;
                        }
                    }
                    message = $"Purchase precheck unavailable ({ex.GetType().Name}); no native purchase was invoked";
                    return JewelPurchaseResult.Rejected;
                }
                message = $"Purchase result temporarily unavailable ({ex.GetType().Name}); pending charge retained without retry";
                return JewelPurchaseResult.Pending;
            }
            finally { _executing = false; }
        }
    }

    internal static void ReconcileLivePurchase()
    {
        lock (Gate)
        {
            if (_executing || _haltReason != null) return;
            try { TryConfirm(); }
            catch
            {
                // A temporary missing native component must not disable future reconciliation.
                // SaveJournal itself halts spending if persistence, rather than observation, fails.
            }
        }
    }

    private static bool TryConfirm()
    {
        var pending = _ledger?.Pending;
        // Cross-restart automatic reconciliation is unsafe without a live action identity.
        if (pending == null || pending.Token != _liveToken || _liveObservation == null || _liveConfirmation == null) return false;
        var after = _liveObservation();
        if (!Equal(pending.BalanceBefore - after.Balance, pending.Cost) || !_liveConfirmation()) return false;
        var confirmed = _ledger.Copy();
        confirmed.Pending = null;
        Complete(confirmed);
        return true;
    }

    private static void Complete(JewelBudgetJournal.Ledger target)
    {
        // Preserve the pending in-memory record if finalization fails. A crash after a successful
        // atomic finalization is safe: both the observed result and charged budget are durable.
        SaveJournal(target);
        _ledger = target;
        _liveToken = null;
        _liveObservation = null;
        _liveConfirmation = null;
    }

    private static bool CheckLimits(JewelPurchaseKind kind, double price, double balance,
        JewelBudgetJournal.Ledger ledger, out string reason)
    {
        reason = "";
        if (!Enabled(kind) || Plugin.IsApplicationQuitting) { reason = "Automatic payment is disabled"; return false; }
        if (string.CompareOrdinal(ledger.Date, Today) > 0) { reason = "Stored budget date is in the future"; return false; }
        var config = Plugin.Config;
        var globalBudget = config.GlobalDailyJewelBudget.Value;
        var categoryBudget = kind == JewelPurchaseKind.Refinery ? config.RefineryDailyJewelBudget.Value : globalBudget;
        var reserve = Math.Max(config.GlobalJewelReserve.Value, Math.Max(config.RefineryJewelReserve.Value, config.PaidSoulsJewelReserve.Value));
        var today = CurrentDay(ledger);
        var categorySpent = kind == JewelPurchaseKind.Refinery ? today.RefinerySpent : today.PaidSoulsSpent;
        if (!double.IsFinite(price) || price <= 0 || !double.IsFinite(balance) || balance < 0 ||
            globalBudget <= 0 || categoryBudget <= 0 || reserve < 0 ||
            price + today.Spent > globalBudget + Epsilon || price + categorySpent > categoryBudget + Epsilon ||
            balance - price + Epsilon < reserve)
        { reason = "Global/category daily budget or shared Jewel reserve would be exceeded"; return false; }
        return true;
    }
    private static bool Enabled(JewelPurchaseKind kind) => Plugin.Config?.Enabled.Value == true &&
        (kind == JewelPurchaseKind.PaidSouls ? Plugin.Config.PaidSoulsChestBurstEnabled.Value :
            Plugin.Config.SupplementalRefiningEnabled.Value &&
            (Plugin.Config.RefineryBuySlotsEnabled.Value || Plugin.Config.RefinerySpeedupEnabled.Value));
    private static void EnsureLoaded() => _ledger ??= LoadJournal();
    private static JewelBudgetJournal.Ledger LoadJournal()
    {
        try { return Journal.Load(); }
        catch (Exception ex) { Halt(ex); throw; }
    }
    private static void SaveJournal(JewelBudgetJournal.Ledger ledger)
    {
        try { Journal.Save(ledger); }
        catch (Exception ex) { Halt(ex); throw; }
    }
    private static JewelBudgetJournal.Ledger CurrentDay(JewelBudgetJournal.Ledger ledger)
    {
        var current = ledger.Copy();
        if (ledger.Date != Today)
        {
            if (ledger.Pending != null) throw new InvalidOperationException("Unresolved charges cannot cross a budget reset.");
            current.Date = Today;
            current.Spent = current.RefinerySpent = current.PaidSoulsSpent = 0;
        }
        return current;
    }
    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static void Halt(Exception ex) => _haltReason = $"Jewel journal/result unavailable ({ex.GetType().Name}); manual review required";
    private static bool Equal(double a, double b) => double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) <= Epsilon;
    private static bool Same(JewelPurchaseObservation a, JewelPurchaseObservation b) =>
        Equal(a.Balance, b.Balance) && Equal(a.Price, b.Price) && a.State == b.State;
}

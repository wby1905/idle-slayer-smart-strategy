using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Il2Cpp;
using MelonLoader;
using MelonLoader.Utils;

namespace SmartStrategy;

internal readonly struct RefineryPremiumLimits
{
    internal double Reserve { get; }
    internal double DailyBudget { get; }
    internal RefineryPremiumLimits(double reserve, double dailyBudget)
    {
        Reserve = reserve;
        DailyBudget = dailyBudget;
    }
}

/// <summary>
/// A fail-closed journal for the verified native refinery charges. Caller-owned switches and
/// benefit estimates do not authorize spending above the supplied reserve or this UTC-day budget.
/// </summary>
internal sealed class RefineryPremiumPolicy
{
    private const string CategoryName = "SmartStrategy_RefineryBudget";
    private const string VerifiedGameSha256 = "E310FE2F86F5C74C54E58F78C04FADD297FF4DF093AE5B21A4227D55958595B7";
    private const double Epsilon = 0.0001;
    private static readonly object Gate = new();
    private static MelonPreferences_Category _category;
    private static MelonPreferences_Entry<string> _date;
    private static MelonPreferences_Entry<double> _spent;
    private static MelonPreferences_Entry<string> _pending;
    private static string _path;
    private static bool _persisted;
    private static bool _executing;
    private static string _haltReason;
    private static long _verifiedLength = -1;
    private static DateTime _verifiedWriteTime;
    private readonly Func<RefineryPremiumLimits> _readLimits;
    private RefineryPremiumLimits? _lastLimits;

    internal RefineryPremiumPolicy() { }

    internal RefineryPremiumPolicy(Func<RefineryPremiumLimits> readLimits) =>
        _readLimits = readLimits ?? throw new ArgumentNullException(nameof(readLimits));

    internal string Status
    {
        get
        {
            lock (Gate)
            {
                try
                {
                    EnsureStore();
                    var ledger = ReadCurrentLedger();
                    var limit = _lastLimits.HasValue ? Format(_lastLimits.Value.DailyBudget) : "not supplied yet";
                    return $"Refinery premium: UTC ledger={ledger.Date}, spent={Format(ledger.Spent)}, " +
                           $"last daily limit={limit}, pending={!string.IsNullOrEmpty(ledger.Pending) || !string.IsNullOrEmpty(_pending.Value)}" +
                           (string.IsNullOrEmpty(_haltReason) ? string.Empty : $"; locked: {_haltReason}");
                }
                catch (Exception ex)
                {
                    return $"Refinery premium: ledger unavailable; payments blocked ({ex.GetType().Name})";
                }
            }
        }
    }

    internal bool TryExecute(RefineryManager manager, int slotIndex, bool unlock, double expectedCost,
        out string message)
    {
        if (_readLimits == null)
        {
            message = "Refinery payment refused: no reserve/budget limits were supplied.";
            return false;
        }
        try { return TryExecute(manager, slotIndex, unlock, expectedCost, _readLimits(), out message); }
        catch (Exception ex)
        {
            message = $"Refinery payment refused: limits are unavailable ({ex.GetType().Name}).";
            return false;
        }
    }

    internal bool TryExecute(RefineryManager manager, int slotIndex, bool unlock, double expectedCost,
        RefineryPremiumLimits limits, out string message)
    {
        lock (Gate)
        {
            _lastLimits = limits;
            if (_executing)
            {
                message = "Refinery payment refused: another refinery payment is being reconciled.";
                return false;
            }
            _executing = true;
            try { return Execute(manager, slotIndex, unlock, expectedCost, limits, out message); }
            catch (Exception ex)
            {
                _haltReason = $"payment/ledger verification failed ({ex.GetType().Name})";
                message = $"Refinery payments blocked: {_haltReason}. No unresolved journal is cleared automatically.";
                return false;
            }
            finally { _executing = false; }
        }
    }

    private static bool Execute(RefineryManager manager, int slotIndex, bool unlock, double expectedCost,
        RefineryPremiumLimits limits, out string message)
    {
        message = string.Empty;
        if (!string.IsNullOrEmpty(_haltReason))
        {
            message = $"Refinery payments blocked: {_haltReason}.";
            return false;
        }
        if (Plugin.IsApplicationQuitting || manager == null ||
            Upgrades.list?.BookOfRefinement?.bought != true || PaidSoulsPurchaseCompatibility.HasUnresolvedPurchase)
        {
            message = "Refinery payment refused: game/refinery is not ready or another Jewel purchase is unresolved.";
            return false;
        }
        if (!NonNegativeFinite(limits.Reserve) || !NonNegativeFinite(limits.DailyBudget) ||
            limits.DailyBudget <= 0 || !double.IsFinite(expectedCost) || expectedCost <= 0)
        {
            message = "Refinery payment refused: a positive budget/quote and a valid reserve are required.";
            return false;
        }

        EnsureStore();
        var ledger = ReadCurrentLedger();
        if (!string.IsNullOrEmpty(ledger.Pending) || !string.IsNullOrEmpty(_pending.Value))
        {
            message = "Refinery payments blocked by a persisted unresolved charge. Inspect the refinery and budget journal; it will not be cleared automatically.";
            return false;
        }
        VerifyNativeBinary();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (string.CompareOrdinal(ledger.Date, today) > 0)
        {
            message = "Refinery payment refused: the stored UTC budget date is in the future.";
            return false;
        }
        var previousSpent = ledger.Date == today ? ledger.Spent : 0;
        var before = Capture(manager);
        var quote = Quote(manager, before, slotIndex, unlock);
        if (!double.IsFinite(quote) || quote <= 0 || !Equal(quote, expectedCost))
        {
            message = "Refinery payment deferred: the native price or target changed; recalculate the benefit before trying again.";
            return false;
        }
        var reservedSpent = previousSpent + quote;
        if (!NonNegativeFinite(reservedSpent) || reservedSpent > limits.DailyBudget + Epsilon ||
            before.Balance - quote + Epsilon < limits.Reserve)
        {
            message = "Refinery payment refused: the current Jewel reserve or refinery-only UTC daily budget would be exceeded.";
            return false;
        }

        var pending = JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString("N"), utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            action = unlock ? "unlock" : "speedup", slot = slotIndex, cost = quote,
            balanceBefore = before.Balance, slotsBefore = before.Unlocked
        });
        var reserved = new Ledger(today, reservedSpent, pending);

        // MelonLoader SaveToFile catches I/O errors internally. Only a matching on-disk readback
        // (and an OS flush) authorizes calling the native operation, never its void return value.
        try { SaveVerified(reserved); }
        catch (Exception ex)
        {
            _haltReason = $"unable to durably reserve refinery payment ({ex.GetType().Name}); native payment was not called";
            message = _haltReason;
            return false;
        }

        // Preference events may run other code synchronously. A changed balance, queue or quote
        // after journaling is not grounds for trusting the old observation or issuing a charge.
        try
        {
            if (Plugin.IsApplicationQuitting || PaidSoulsPurchaseCompatibility.HasUnresolvedPurchase ||
                !SameState(before, Capture(manager)) || !Equal(Quote(manager, before, slotIndex, unlock), quote))
            {
                if (!FinalizeLedger(new Ledger(today, previousSpent, string.Empty), reserved, out var failure))
                    message = $"Native refinery payment was not called; {failure}";
                else message = "Refinery payment deferred: state changed while persisting the reservation; no native payment was called.";
                return false;
            }
        }
        catch (Exception ex)
        {
            _haltReason = $"post-journal state could not be verified ({ex.GetType().Name}); pending reservation retained";
            message = _haltReason;
            return false;
        }

        bool accepted;
        State after;
        try
        {
            accepted = unlock ? manager.TryUnlockSlot() : manager.ExecuteSpeedUp(slotIndex);
            after = Capture(manager);
        }
        catch (Exception ex)
        {
            _haltReason = $"native refinery charge may have occurred ({ex.GetType().Name}); pending reservation retained";
            message = $"Refinery payments blocked: {_haltReason}. Free refining remains available.";
            return false;
        }

        if (!accepted && SameState(before, after))
        {
            if (!FinalizeLedger(new Ledger(today, previousSpent, string.Empty), reserved, out var failure))
                message = $"Native refinery payment was rejected without a state change; {failure}";
            else message = "Native refinery payment was rejected without charging or changing slots; the reservation was released.";
            return false;
        }
        if (!accepted || !Equal(before.Balance - after.Balance, quote) ||
            !ExpectedResult(before, after, slotIndex, unlock))
        {
            _haltReason = "native return, exact Jewel debit and refinery result did not agree; pending reservation retained";
            message = $"Refinery payments blocked: {_haltReason}. Free refining remains available.";
            return false;
        }

        if (!FinalizeLedger(new Ledger(today, reservedSpent, string.Empty), reserved, out var finalizationFailure))
        {
            message = $"Refinery {(unlock ? "slot unlock" : "speedup")} was confirmed for {Format(quote)} Jewels, " +
                      $"but {finalizationFailure}";
            return true; // The paid game action is verified; later payments remain locked by the journal.
        }
        message = $"Refinery {(unlock ? "slot unlock" : "speedup")} confirmed: {Format(quote)} Jewels; " +
                  $"UTC daily spend {Format(reservedSpent)}/{Format(limits.DailyBudget)}.";
        return true;
    }

    private static double Quote(RefineryManager manager, State state, int slotIndex, bool unlock)
    {
        if (unlock)
            return slotIndex == -1 && state.Unlocked < state.Slots.Length
                ? RefineryManager.UNLOCK_SLOT_COST : 0;
        if (slotIndex < 0 || slotIndex >= state.Unlocked || slotIndex >= state.Slots.Length ||
            string.IsNullOrEmpty(state.Slots[slotIndex].Material) || state.Slots[slotIndex].Remaining <= 0)
            return 0;
        return manager.GetSpeedUpCost(slotIndex);
    }

    private static State Capture(RefineryManager manager)
    {
        if (manager == null || manager != RefineryManager.instance || PlayerInventory.instance == null)
            throw new InvalidOperationException("Refinery or inventory instance changed.");
        var source = manager.slots;
        var unlocked = manager.unlockedSlots;
        if (source == null || source.Length != RefineryManager.MAX_SLOTS || unlocked < 1 || unlocked > source.Length)
            throw new InvalidOperationException("Invalid refinery slot layout.");
        var slots = new SlotState[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            var slot = source[i] ?? throw new InvalidOperationException("Refinery slots are not initialized.");
            if (!NonNegativeFinite(slot.totalAmount) || !NonNegativeFinite(slot.remainingTime) ||
                !NonNegativeFinite(slot.amountClaimed)) throw new InvalidOperationException("Invalid refinery queue values.");
            var material = slot.material;
            slots[i] = new SlotState(slot.Pointer, material == null ? string.Empty :
                material.id ?? $"instance:{material.GetInstanceID()}", slot.totalAmount, slot.remainingTime, slot.amountClaimed);
        }
        var balance = PlayerInventory.instance.jewelsOfSoul;
        if (!NonNegativeFinite(balance)) throw new InvalidOperationException("Invalid live Jewel balance.");
        return new State(manager.GetInstanceID(), unlocked, balance, slots);
    }

    private static bool SameState(State left, State right) => left.ManagerId == right.ManagerId &&
        left.Unlocked == right.Unlocked && Equal(left.Balance, right.Balance) && SameSlots(left, right);

    private static bool SameSlots(State left, State right, int speedupSlot = -1)
    {
        if (left.Slots.Length != right.Slots.Length) return false;
        for (var i = 0; i < left.Slots.Length; i++)
        {
            var a = left.Slots[i];
            var b = right.Slots[i];
            if (a.Pointer != b.Pointer || a.Material != b.Material || !Equal(a.Total, b.Total) ||
                !Equal(a.Claimed, b.Claimed) || (i == speedupSlot ? b.Remaining > Epsilon : !Equal(a.Remaining, b.Remaining)))
                return false;
        }
        return true;
    }

    private static bool ExpectedResult(State before, State after, int slotIndex, bool unlock) =>
        before.ManagerId == after.ManagerId && (unlock
            ? after.Unlocked == before.Unlocked + 1 && SameSlots(before, after)
            : after.Unlocked == before.Unlocked && SameSlots(before, after, slotIndex));

    private static void EnsureStore()
    {
        if (_category != null) return;
        _path = Path.Combine(MelonEnvironment.UserDataDirectory, CategoryName + ".cfg");
        var initial = File.Exists(_path) ? ReadDisk(flush: false) : new Ledger(string.Empty, 0, string.Empty);
        _persisted = File.Exists(_path);
        _category = MelonPreferences.GetCategory(CategoryName) ??
                    MelonPreferences.CreateCategory(CategoryName, "Refinery budget journal", true, false);
        _category.SetFilePath(_path, autoload: false, printmsg: false);
        _date = _category.GetEntry<string>("date") ?? _category.CreateEntry("date", string.Empty, is_hidden: true);
        _spent = _category.GetEntry<double>("spent") ?? _category.CreateEntry("spent", 0d, is_hidden: true);
        _pending = _category.GetEntry<string>("pending") ?? _category.CreateEntry("pending", string.Empty, is_hidden: true);
        SetEntries(initial);
    }

    private static Ledger ReadCurrentLedger()
    {
        if (File.Exists(_path)) return ReadDisk(flush: false);
        if (_persisted) throw new IOException("An existing refinery budget journal is missing.");
        return new Ledger(string.Empty, 0, string.Empty);
    }

    private static void SetEntries(Ledger state)
    {
        _date.Value = state.Date;
        _spent.Value = state.Spent;
        _pending.Value = state.Pending;
    }

    private static void SaveVerified(Ledger state)
    {
        SetEntries(state);
        _category.SaveToFile(printmsg: false);
        var saved = ReadDisk(flush: true);
        if (saved.Date != state.Date || !Equal(saved.Spent, state.Spent) || saved.Pending != state.Pending)
            throw new IOException("Refinery budget journal readback did not match the requested state.");
        _persisted = true;
    }

    private static bool FinalizeLedger(Ledger final, Ledger reserved, out string failure)
    {
        failure = string.Empty;
        try { SaveVerified(final); return true; }
        catch (Exception ex)
        {
            // Restore the unresolved reservation in memory and, if possible, on disk. A malformed
            // or inaccessible disk journal also blocks the next session instead of resetting spend.
            SetEntries(reserved);
            try { SaveVerified(reserved); } catch { /* Keep the in-memory lock and pending entry. */ }
            _haltReason = $"budget finalization failed ({ex.GetType().Name}); manual reconciliation is required";
            failure = $"{_haltReason}. Further refinery payments are blocked.";
            return false;
        }
    }

    private static Ledger ReadDisk(bool flush)
    {
        using var file = new FileStream(_path, FileMode.Open, flush ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read);
        if (file.Length <= 0 || file.Length > 65536) throw new InvalidDataException("Invalid refinery budget journal length.");
        using var reader = new StreamReader(file, Encoding.UTF8, true, 1024, leaveOpen: true);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var inCategory = false;
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('['))
            {
                inCategory = line == $"[{CategoryName}]";
                continue;
            }
            if (!inCategory) continue;
            var equals = line.IndexOf('=');
            if (equals <= 0) throw new InvalidDataException("Invalid refinery budget entry.");
            var key = line[..equals].Trim();
            if (key is not ("date" or "spent" or "pending")) continue;
            if (!values.TryAdd(key, line[(equals + 1)..].Trim())) throw new InvalidDataException("Duplicate refinery budget entry.");
        }
        if (values.Count != 3 || !values.TryGetValue("date", out var rawDate) ||
            !values.TryGetValue("spent", out var rawSpent) || !values.TryGetValue("pending", out var rawPending))
            throw new InvalidDataException("Incomplete refinery budget journal.");
        // This is the deliberately narrow schema emitted by MelonPreferences: quoted strings
        // and one finite number. Unexpected TOML forms fail closed rather than guessing a budget.
        var date = ReadQuotedString(rawDate);
        var pending = ReadQuotedString(rawPending);
        if (!double.TryParse(rawSpent, NumberStyles.Float, CultureInfo.InvariantCulture, out var spent) ||
            !NonNegativeFinite(spent) || date.Length != 0 && !DateTime.TryParseExact(date, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _) || date.Length == 0 && (spent > 0 || pending.Length > 0))
            throw new InvalidDataException("Invalid refinery budget date or amount.");
        if (flush) file.Flush(flushToDisk: true);
        return new Ledger(date, spent, pending);
    }

    private static string ReadQuotedString(string value)
    {
        // Tomlet 6.2 emits JSON-shaped strings as TOML single-quoted literals to avoid escaping
        // their double quotes. Dates and empty strings use the ordinary double-quoted form.
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            var literal = value[1..^1];
            if (literal.Contains('\'')) throw new InvalidDataException("Unsupported multiline/literal budget string.");
            return literal;
        }
        return JsonSerializer.Deserialize<string>(value) ?? throw new InvalidDataException("Missing budget string.");
    }

    private static void VerifyNativeBinary()
    {
        var path = Path.Combine(MelonEnvironment.GameRootDirectory, "GameAssembly.dll");
        var info = new FileInfo(path);
        if (info.Exists && info.Length == _verifiedLength && info.LastWriteTimeUtc == _verifiedWriteTime) return;
        var length = info.Length;
        var writeTime = info.LastWriteTimeUtc;
        using var file = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var hash = Convert.ToHexString(sha256.ComputeHash(file));
        info.Refresh();
        if (hash != VerifiedGameSha256 || info.Length != length || info.LastWriteTimeUtc != writeTime)
            throw new NotSupportedException("The native refinery payment implementation has not been verified for this game binary.");
        _verifiedLength = length;
        _verifiedWriteTime = writeTime;
    }

    private static bool NonNegativeFinite(double value) => double.IsFinite(value) && value >= 0;
    private static bool Equal(double left, double right) =>
        double.IsFinite(left) && double.IsFinite(right) && Math.Abs(left - right) <= Epsilon;
    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private sealed class Ledger
    {
        internal string Date { get; }
        internal double Spent { get; }
        internal string Pending { get; }
        internal Ledger(string date, double spent, string pending)
        {
            Date = date;
            Spent = spent;
            Pending = pending;
        }
    }

    private sealed class SlotState
    {
        internal nint Pointer { get; }
        internal string Material { get; }
        internal double Total { get; }
        internal double Remaining { get; }
        internal double Claimed { get; }
        internal SlotState(nint pointer, string material, double total, double remaining, double claimed)
        {
            Pointer = pointer;
            Material = material;
            Total = total;
            Remaining = remaining;
            Claimed = claimed;
        }
    }

    private sealed class State
    {
        internal int ManagerId { get; }
        internal int Unlocked { get; }
        internal double Balance { get; }
        internal SlotState[] Slots { get; }
        internal State(int managerId, int unlocked, double balance, SlotState[] slots)
        {
            ManagerId = managerId;
            Unlocked = unlocked;
            Balance = balance;
            Slots = slots;
        }
    }
}

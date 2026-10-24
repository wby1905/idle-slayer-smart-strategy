using System.Text.Json;
using Il2Cpp;

namespace SmartStrategy;

// Native-only adapter. The shared coordinator owns budgets, journals and cross-consumer locking.
internal sealed class RefineryPremiumPolicy
{
    private const double Epsilon = 0.0001;
    internal string Status => JewelPurchaseCoordinator.Status;

    internal bool TryExecute(RefineryManager manager, int slotIndex, bool unlock, double expectedCost, out string message)
    {
        message = "Refinery payment refused";
        try
        {
            if (!CanInvoke(manager, slotIndex, unlock)) return false;
            var before = Capture(manager);
            if (!Equal(Quote(manager, before, slotIndex, unlock), expectedCost)) return false;
            JewelPurchaseObservation Observe()
            {
                var state = Capture(manager);
                var fingerprint = JsonSerializer.Serialize(new
                {
                    state.ManagerId, state.Unlocked,
                    slots = state.Slots.Select(s => new { pointer = (long)s.Pointer, s.Material, s.Total, s.Remaining, s.Claimed })
                });
                return new JewelPurchaseObservation(state.Balance, Quote(manager, state, slotIndex, unlock), fingerprint);
            }
            var result = JewelPurchaseCoordinator.Execute(JewelPurchaseKind.Refinery, expectedCost,
                unlock ? "Refinery slot unlock" : $"Refinery slot {slotIndex + 1} acceleration",
                Observe, () => unlock ? manager.TryUnlockSlot() : manager.ExecuteSpeedUp(slotIndex),
                () => ExpectedResult(before, Capture(manager), slotIndex, unlock), out message,
                canInvoke: () => CanInvoke(manager, slotIndex, unlock));
            return result == JewelPurchaseResult.Confirmed;
        }
        catch (Exception ex)
        {
            message = $"Refinery native payment unavailable: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool CanInvoke(RefineryManager manager, int slotIndex, bool unlock)
    {
        var config = Plugin.Config;
        if (Plugin.IsApplicationQuitting || config?.Enabled.Value != true ||
            !config.SupplementalRefiningEnabled.Value || manager == null ||
            manager != RefineryManager.instance || PlayerInventory.instance == null ||
            Upgrades.list?.BookOfRefinement?.bought != true) return false;
        var slots = manager.slots;
        var unlocked = manager.unlockedSlots;
        if (slots == null || slots.Length != RefineryManager.MAX_SLOTS || unlocked < 1 || unlocked > slots.Length)
            return false;
        if (!unlock)
            return config.RefinerySpeedupEnabled.Value && slotIndex >= 0 && slotIndex < unlocked &&
                   slots[slotIndex]?.material != null && PositiveFinite(slots[slotIndex].remainingTime);

        var maximum = Math.Min(RefineryManager.MAX_SLOTS, Math.Clamp(config.RefineryMaximumSlots.Value, 1, 3));
        if (!config.RefineryBuySlotsEnabled.Value || slotIndex != -1 || unlocked >= maximum || unlocked >= slots.Length)
            return false;
        var next = slots[unlocked];
        return next != null && !next.IsActive && next.material == null &&
               Equal(next.totalAmount, 0) && Equal(next.amountClaimed, 0) && Equal(next.remainingTime, 0);
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

    private static bool SameSlots(State left, State right, int speedupSlot = -1, bool allowNormalCountdown = false)
    {
        if (left.Slots.Length != right.Slots.Length) return false;
        for (var i = 0; i < left.Slots.Length; i++)
        {
            var a = left.Slots[i];
            var b = right.Slots[i];
            var timerMatches = i == speedupSlot
                ? NonNegativeFinite(b.Remaining) && b.Remaining <= Epsilon
                : allowNormalCountdown
                    ? NonNegativeFinite(b.Remaining) && b.Remaining <= a.Remaining + Epsilon
                    : Equal(a.Remaining, b.Remaining);
            if (a.Pointer != b.Pointer || a.Material != b.Material || !Equal(a.Total, b.Total) ||
                !Equal(a.Claimed, b.Claimed) || !timerMatches)
                return false;
        }
        return true;
    }

    // A later confirmation may observe ordinary countdowns. This relaxation is exclusive to
    // confirmed-result checks; pre-invocation and rejected-payment snapshots remain exact.
    private static bool ExpectedResult(State before, State after, int slotIndex, bool unlock) =>
        before.ManagerId == after.ManagerId && (unlock
            ? after.Unlocked == before.Unlocked + 1 && SameSlots(before, after, allowNormalCountdown: true)
            : after.Unlocked == before.Unlocked && SameSlots(before, after, slotIndex, allowNormalCountdown: true));

    private static bool NonNegativeFinite(double value) => double.IsFinite(value) && value >= 0;
    private static bool PositiveFinite(double value) => double.IsFinite(value) && value > Epsilon;
    private static bool Equal(double left, double right) =>
        double.IsFinite(left) && double.IsFinite(right) && Math.Abs(left - right) <= Epsilon;
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

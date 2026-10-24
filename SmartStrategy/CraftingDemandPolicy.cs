using Il2Cpp;
using UnityEngine;

namespace SmartStrategy;

internal readonly record struct CraftingStockDecision(int Copies, bool Farm, bool Premium, string Reason);

// Estimates opportunity-weighted benefit in baseline productive seconds. Unknown probability
// models never authorize dedicated farming or premium spending. Observations are runner time,
// not wall-clock/offline time, and do not change AFK permanent effects.
internal sealed class CraftingDemandPolicy
{
    private readonly Dictionary<SmartItem, Observation> _observations = new();
    private const double MinimumObservedSeconds = 60;

    internal void Observe(SmartItem kind, bool opportunity)
    {
        var state = Get(kind);
        var now = Time.unscaledTime;
        var elapsed = state.LastObservation < 0 ? 0 : Math.Clamp(now - state.LastObservation, 0, 5);
        state.LastObservation = now;
        state.RunnerClock += elapsed;
        var decay = Math.Exp(-elapsed / 3600);
        state.Observed = state.Observed * decay + elapsed;
        state.Opportunity = state.Opportunity * decay + (opportunity ? elapsed : 0);
    }

    internal void Crafted(SmartItem kind)
    {
        var state = Get(kind);
        var now = state.RunnerClock;
        if (state.LastCraft >= 0 && now > state.LastCraft)
        {
            var interval = now - state.LastCraft;
            state.Interval = state.Samples == 0 ? interval : state.Interval * 0.7 + interval * 0.3;
            state.Samples++;
        }
        state.LastCraft = now;
    }

    internal CraftingStockDecision Decide(SmartItem kind, TemporaryCraftableItem item, int retainedCopies,
        bool immediate, double? deadline, double coverage, Func<int, CraftSupplyQuote> quoteFactory)
    {
        var baseCopies = 1 + Math.Clamp(retainedCopies, 0, 9);
        var duration = item.GetDuration();
        if (!double.IsFinite(duration) || duration <= 0) return new(0, false, false, "No timed coverage");
        var state = Get(kind);
        var horizon = Math.Max(0, Plugin.Config.RefineryLookaheadMinutes.Value * 60);
        if (coverage > Math.Max(horizon, state.LastLead * 1.2) && Time.unscaledTime < state.NextLeadPoll)
            return new(0, false, false, "Coverage exceeds supply lead time");
        var quote = quoteFactory(baseCopies);
        state.NextLeadPoll = Time.unscaledTime + 30;
        if (quote.Reachable) state.LastLead = quote.ReadySeconds;
        if (coverage > Math.Max(horizon, state.LastLead * 1.2)) return new(0, false, false, "Coverage exceeds supply lead time");
        var known = CraftableYieldModel.TryMarginalGain(kind, item, out var gain, out var modelReason);
        if (known && gain <= 0) return new(0, false, false, "No marginal benefit at current native attributes");
        if (kind == SmartItem.DeathBringer && known) gain /= 1 + gain;
        var premium = known && gain > 0 && immediate;
        if (!known || !quote.Reachable || state.Observed < MinimumObservedSeconds)
            return new(baseCopies, false, premium, !known ? modelReason : !quote.Reachable ? quote.BlockedReason : "Learning opportunity frequency; surplus inputs only");
        var duty = Math.Clamp(state.Opportunity / Math.Max(1, state.Observed), 0, 1);
        var useful = Math.Min(duration, Math.Max(0, Math.Max(duration, horizon) - quote.ReadySeconds)) * duty;
        if (immediate && deadline.HasValue) useful = Math.Max(useful, Math.Max(0, deadline.Value - quote.ReadySeconds));
        var productionCost = Math.Max(quote.FarmingSeconds,
            quote.RefineryWorkSeconds / Math.Max(1, RefineryManager.instance?.unlockedSlots ?? 1));
        var benefit = useful * Math.Max(0, gain);
        var minimumSaving = Math.Max(0, Plugin.Config.QuestSupplyMinimumNetSeconds.Value);
        if (benefit < productionCost + minimumSaving)
            return new(baseCopies, false, premium, $"Surplus only: estimated benefit {benefit:0}s <= supply cost {productionCost:0}s plus margin");

        var copies = baseCopies;
        // Keep enough uses for observed replenishment latency, not an arbitrary bag-full target.
        // Additional reserves require actual consumption samples and a separate full-recipe quote.
        if (state.Samples >= 2 && state.Interval > 0)
        {
            var wantedUses = 1 + (int)Math.Clamp(Math.Ceiling(Math.Max(0, quote.ReadySeconds - coverage) / state.Interval), 0, 2);
            var expanded = Math.Min(10, retainedCopies + wantedUses);
            if (expanded > copies)
            {
                var bigger = quoteFactory(expanded);
                var biggerCost = Math.Max(bigger.FarmingSeconds,
                    bigger.RefineryWorkSeconds / Math.Max(1, RefineryManager.instance?.unlockedSlots ?? 1));
                if (bigger.Reachable && biggerCost + minimumSaving < benefit * wantedUses) copies = expanded;
            }
        }
        return new(copies, true, premium,
            $"Observed opportunity={duty:P0}, benefit={benefit:0}s, supply cost={productionCost:0}s, lead={quote.ReadySeconds:0}s");
    }

    // Cached observations may survive an ordinary poll, but explicit mode/settings changes reset
    // learned context. Inventory/queue quantities are always quoted live; only map profiles cache.
    internal void Invalidate() => _observations.Clear();
    private Observation Get(SmartItem kind)
    {
        if (!_observations.TryGetValue(kind, out var state)) _observations[kind] = state = new Observation();
        return state;
    }
    private sealed class Observation
    {
        internal float LastObservation = -1, NextLeadPoll;
        internal double LastCraft = -1, RunnerClock, Observed, Opportunity, Interval, LastLead;
        internal int Samples;
    }
}

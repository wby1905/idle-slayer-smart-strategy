using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace SmartStrategy;

internal static class PermanentClientBonuses
{
    internal const double PlanningCoverageSeconds = 31536000d;

    private static readonly HashSet<int> ManagedRandomEvents = new();
    private static readonly HashSet<int> ManagedTimedCraftables = new();

    internal static int ManagedRandomEventCount => Plugin.EndgameModeActive ? ManagedRandomEvents.Count : 0;
    internal static int ManagedTimedCraftableCount => Plugin.EndgameModeActive ? ManagedTimedCraftables.Count : 0;

    internal static bool IsManagedRandomEvent(RandomEvent randomEvent)
    {
        if (randomEvent == null || Plugin.Config == null || !Plugin.EndgameModeActive) return false;
        var paid = IsPaidBonus(randomEvent);
        if (paid && !Plugin.Config.EndgamePermanentPaidBonusesEnabled.Value) return false;
        if (!paid && !Plugin.Config.PermanentFreeMultipliersEnabled.Value) return false;
        if (IsSoulsBonus(randomEvent) && Plugin.Config.PermanentFreeSoulsBonusEnabled.Value != true) return false;
        try { return ManagedRandomEvents.Contains(randomEvent.GetInstanceID()); }
        catch { return false; }
    }

    internal static bool IsManagedTimedCraftable(TemporaryCraftableItem item)
    {
        if (item == null || !Plugin.EndgameModeActive ||
            Plugin.Config?.PermanentTimedCraftableEffectsEnabled.Value != true) return false;
        try { return ManagedTimedCraftables.Contains(item.GetInstanceID()); }
        catch { return false; }
    }

    internal static bool IsSupportedPersistentRandomEvent(RandomEvent randomEvent)
    {
        if (randomEvent == null) return false;
        try
        {
            return randomEvent.TryCast<SoulsBonus>() != null ||
                   randomEvent.TryCast<CpSBonus>() != null ||
                   randomEvent.TryCast<CoinValueBonus>() != null ||
                   randomEvent.TryCast<EquipmentBonus>() != null ||
                   randomEvent.TryCast<GemstoneRush>() != null ||
                   randomEvent.TryCast<Frenzy>() != null;
        }
        catch { return false; }
    }

    internal static bool IsSoulsBonus(RandomEvent randomEvent)
    {
        try { return randomEvent?.TryCast<SoulsBonus>() != null; }
        catch { return false; }
    }

    internal static bool IsPaidBonus(RandomEvent randomEvent)
    {
        try
        {
            return randomEvent?.TryCast<PaidSoulsBonus>() != null ||
                   randomEvent?.TryCast<PaidCpSBonus>() != null;
        }
        catch { return false; }
    }

    internal static string RandomEventTypeName(RandomEvent randomEvent)
    {
        if (randomEvent == null) return "UnknownRandomEvent";
        try
        {
            if (randomEvent.TryCast<SoulsBonus>() != null) return nameof(SoulsBonus);
            if (randomEvent.TryCast<CpSBonus>() != null) return nameof(CpSBonus);
            if (randomEvent.TryCast<CoinValueBonus>() != null) return nameof(CoinValueBonus);
            if (randomEvent.TryCast<EquipmentBonus>() != null) return nameof(EquipmentBonus);
            if (randomEvent.TryCast<GemstoneRush>() != null) return nameof(GemstoneRush);
            if (randomEvent.TryCast<Frenzy>() != null) return nameof(Frenzy);
            if (randomEvent.TryCast<PaidSoulsBonus>() != null) return nameof(PaidSoulsBonus);
            if (randomEvent.TryCast<PaidCpSBonus>() != null) return nameof(PaidCpSBonus);
        }
        catch { }
        return randomEvent.GetType().Name;
    }

    internal static double CoverageSeconds(TemporaryCraftableItem item)
    {
        if (IsManagedTimedCraftable(item)) return PlanningCoverageSeconds;
        try { return Math.Max(0, item?.currentTime ?? 0); }
        catch { return 0; }
    }

    internal static void ManageRandomEvent(RandomEvent randomEvent)
    {
        if (randomEvent == null) return;
        ManagedRandomEvents.Add(randomEvent.GetInstanceID());
    }

    internal static void ManageTimedCraftable(TemporaryCraftableItem item)
    {
        if (item == null) return;
        ManagedTimedCraftables.Add(item.GetInstanceID());
    }
}

[HarmonyPatch(typeof(RandomEvent), nameof(RandomEvent.DecreaseTime))]
internal static class PermanentRandomEventTimerPatch
{
    private static bool Prefix(RandomEvent __instance) =>
        !PermanentClientBonuses.IsManagedRandomEvent(__instance);
}

[HarmonyPatch(typeof(TemporaryCraftableItem), nameof(TemporaryCraftableItem.DecreaseTime))]
internal static class PermanentTimedCraftableTimerPatch
{
    private static bool Prefix(TemporaryCraftableItem __instance) =>
        !PermanentClientBonuses.IsManagedTimedCraftable(__instance);
}

public sealed partial class StrategyBehaviour
{
    private readonly HashSet<int> _loggedPermanentRandomEvents = new();
    private readonly HashSet<int> _loggedPermanentTimedCraftables = new();
    private float _nextPermanentTimedCraftableScanAt;

    private void EnsurePermanentClientBonuses(bool stable)
    {
        EnsurePermanentRandomMultipliers(stable);
        EnsurePermanentTimedCraftables(stable);
    }

    private void EnsurePermanentRandomMultipliers(bool stable)
    {
        if (!Plugin.EndgameModeActive || !Plugin.Config.PermanentFreeMultipliersEnabled.Value)
        {
            _permanentFreeSoulsEventOwned = false;
            return;
        }

        try
        {
            var manager = RandomEventManager.instance;
            if (manager == null) return;
            var seen = new HashSet<int>();
            var events = manager.randomEvents;
            if (events != null)
                foreach (var randomEvent in events)
                    EnsureOnePermanentRandomEvent(manager, randomEvent, stable, seen);

        // Current versions store Souls and fixed 9x CpS in separate authoritative fields. Include them in the
        // same lifecycle even when they do not appear in the randomEvents array.
            EnsureOnePermanentRandomEvent(manager, manager.soulsBonus, stable, seen);
            EnsureOnePermanentRandomEvent(manager, manager.cpsBonus9x, stable, seen);
            EnsureOnePermanentRandomEvent(manager, manager.dailyCpSBonus, stable, seen);
            EnsureOnePermanentRandomEvent(manager, manager.frenzy, stable, seen);

            var souls = manager.soulsBonus;
            _permanentFreeSoulsEventOwned = souls != null &&
                                             PermanentClientBonuses.IsManagedRandomEvent(souls);
        }
        catch (Exception ex)
        {
            _permanentFreeSoulsEventOwned = false;
            _nextPermanentFreeSoulsAttemptAt = Time.unscaledTime + 10f;
            if (Plugin.Config.DebugLogging.Value)
                SmartLog.Warning(SmartLogCategory.Endgame,
                    $"Permanent free-multiplier maintenance failed; retrying in 10 seconds: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void EnsureOnePermanentRandomEvent(RandomEventManager manager, RandomEvent randomEvent,
        bool stable, HashSet<int> seen)
    {
        if (randomEvent == null || !PermanentClientBonuses.IsSupportedPersistentRandomEvent(randomEvent)) return;
        if (PermanentClientBonuses.IsSoulsBonus(randomEvent) &&
            !Plugin.Config.PermanentFreeSoulsBonusEnabled.Value) return;
        try
        {
            var id = randomEvent.GetInstanceID();
            if (!seen.Add(id)) return;

            var active = randomEvent.IsActive();
            if (!active)
            {
                if (!stable || !QuestDirector.IsStandardAvailableMap(MapController.instance?.selectedMap) ||
                    Time.unscaledTime < _nextPermanentFreeSoulsAttemptAt) return;
                if (!manager.CheckEventRequirements(randomEvent)) return;
                randomEvent.Activate(true, true, 0f, RandomBox.Type.Gold, null);
                active = randomEvent.IsActive();
            }
            if (!active) return;

            PermanentClientBonuses.ManageRandomEvent(randomEvent);
            if (_loggedPermanentRandomEvents.Add(id))
                SmartLog.Debug(SmartLogCategory.Endgame,
                    $"Activated permanent free multiplier {PermanentClientBonuses.RandomEventTypeName(randomEvent)} and froze its client timer.");
        }
        catch (Exception ex)
        {
            _nextPermanentFreeSoulsAttemptAt = Time.unscaledTime + 10f;
            if (Plugin.Config.DebugLogging.Value)
                SmartLog.Warning(SmartLogCategory.Endgame,
                    $"Unable to activate permanent multiplier {PermanentClientBonuses.RandomEventTypeName(randomEvent)}; " +
                    $"the effect was isolated: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void EnsurePermanentTimedCraftables(bool stable)
    {
        if (!Plugin.EndgameModeActive || !Plugin.Config.PermanentTimedCraftableEffectsEnabled.Value || !stable ||
            Time.unscaledTime < _nextPermanentTimedCraftableScanAt) return;
        _nextPermanentTimedCraftableScanAt = Time.unscaledTime + 60f;

        try
        {
            foreach (var item in Resources.FindObjectsOfTypeAll<TemporaryCraftableItem>())
            {
                if (item == null || item.lootBoxOpen != null || item.requiresFreeArmorySpace) continue;
                double duration;
                try { duration = item.GetDuration(); }
                catch { duration = item.duration; }
                if (!double.IsFinite(duration) || duration <= 0.05) continue;

                bool unlocked;
                try
                {
                    unlocked = item.TabVisible() ||
                        CraftableCatalog.IsSupplementalTimedItem(item) && CraftableCatalog.HasUnlockedRecipe(item);
                }
                catch { unlocked = false; }
                if (!unlocked) continue;

                item.currentTime = Math.Max(Math.Max(0, item.currentTime), duration);
                PermanentClientBonuses.ManageTimedCraftable(item);
                var id = item.GetInstanceID();
                if (_loggedPermanentTimedCraftables.Add(id))
                    SmartLog.Debug(SmartLogCategory.Endgame,
                        $"Activated permanent timed craftable {item.name ?? "unknown"}; native duration={duration:0.#}s, " +
                        "Craft was not called and no materials were consumed.");
            }
        }
        catch (Exception ex)
        {
            _nextPermanentTimedCraftableScanAt = Time.unscaledTime + 10f;
            if (Plugin.Config.DebugLogging.Value)
                SmartLog.Warning(SmartLogCategory.Endgame,
                    $"Permanent timed-craftable scan failed; retrying in 10 seconds: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

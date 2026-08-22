using Il2Cpp;
using UnityEngine;

namespace SmartStrategy;

public sealed partial class StrategyBehaviour
{
    private enum PaidSoulsBurstPhase
    {
        Idle,
        PreparingMaterials,
        TravelingToSoulMap,
        WaitingForChestHunt,
        PurchaseRequested,
        ChestHunt,
        Bursting
    }

    private PaidSoulsBurstPhase _paidSoulsPhase;
    private JewelsOfSoulSoulsBonus _paidSoulsPurchase;
    private PaidSoulsBonus _paidSoulsEffect;
    private SoulMapEfficiency _paidTargetSoulMap;
    private MaterialNeed _paidSupplyNeed;
    private int _paidTargetRagePillUses;
    private int _paidTierMultiplier;
    private double _paidTierCost;
    private double _paidBalanceBeforePurchase;
    private float _paidPurchaseRequestedAt;
    private float _paidNextEligibilityScan;
    private float _paidNextEffectResolveScan;
    private float _paidEffectMissingSince = -1f;
    private float _paidEligibilityBlockedUntil;
    private float _paidNextSupplyScan;
    private float _paidNextSupplyDeepScan;
    private float _paidNextSoulMapScan;
    private float _paidNextUnavailableLogAt;
    private float _paidNextPlateauLogAt;
    private float _paidNextEquipmentLogAt;
    private string _paidLastFocus;
    private string _paidPlateauSummary = "next-USP ratio not read yet";
    private string _paidEquipmentSummary = "highest equipment progress not read yet";
    private double _paidObservedUltraAscensions = double.NaN;
    private double _paidCurrentUaSp;
    private double _paidSpForNextUsp;
    private double _paidNextUspCostRatioPercent = double.PositiveInfinity;
    private bool _paidPurchaseConfirmed;
    private int _paidPurchaseFailures;
    private int _paidRagePillsUsed;
    private bool _paidWaitForChestCompletion;
    private bool _paidChestObservedAfterPurchase;

    private bool PaidSoulsBurstOwnsControl => _paidSoulsPhase != PaidSoulsBurstPhase.Idle;
    internal bool BlocksAutomaticNormalAscension =>
        Plugin.Config?.Enabled.Value == true && !_emergencyStopped &&
        _paidSoulsPhase is PaidSoulsBurstPhase.PurchaseRequested or
            PaidSoulsBurstPhase.ChestHunt or PaidSoulsBurstPhase.Bursting;

    private bool HandlePaidSoulsBurstStrategy(bool stable)
    {
        if (Plugin.EndgameModeActive && Plugin.Config.EndgamePermanentPaidBonusesEnabled.Value)
        {
            if (PaidSoulsBurstOwnsControl)
                FinishPaidSoulsBurst("endgame permanent 500x bonuses took control");
            return false;
        }
        RefreshPaidSoulsUspCostRatio();
        if (!Plugin.Config.PaidSoulsChestBurstEnabled.Value)
        {
            if (PaidSoulsBurstOwnsControl)
                FinishPaidSoulsBurst("the paid Souls burst was disabled by the player");
            return false;
        }

        var effectActive = TryReadPaidSoulsEffect(out var paidSecondsLeft, out var paidMultiplier);
        if (PaidSoulsBurstOwnsControl && effectActive &&
            _paidSoulsPhase is PaidSoulsBurstPhase.PreparingMaterials or
                PaidSoulsBurstPhase.TravelingToSoulMap or PaidSoulsBurstPhase.WaitingForChestHunt)
        {
        // Support another mod or a manual purchase. Once a live PaidSoulsBonus exists, stop supplying,
        // waiting for another Chest Hunt, or attempting another charge.
            BeginAdoptedPaidSoulsBurst(paidSecondsLeft, paidMultiplier);
            return true;
        }
        if (!PaidSoulsBurstOwnsControl)
        {
            // If a reload or another entry point already started the paid multiplier, adopt the remaining Rage chain
            // without traveling mid-window and wasting paid seconds on loading.
            if (effectActive)
            {
                BeginAdoptedPaidSoulsBurst(paidSecondsLeft, paidMultiplier);
                return true;
            }

            if (!stable || Time.unscaledTime < _paidNextEligibilityScan ||
                Time.unscaledTime < _paidEligibilityBlockedUntil) return false;
            _paidNextEligibilityScan = Time.unscaledTime + 5f;
            if (!Plugin.Config.AutomaticMaterialTravel.Value || !Plugin.Config.RagePillEnabled.Value)
            {
                LogPaidUnavailable("material travel and Rage Pills must both be enabled");
                return false;
            }
            if (!TryResolveHighestPaidSoulsTier(out var purchase, out var effect, out var tier, out var cost))
            {
                _paidNextEligibilityScan = Time.unscaledTime + 60f;
                return false;
            }
            if (!PaidSoulsTierAccepted(tier)) return false;
            var balance = CurrentJewelsOfSoul();
            var reserve = Math.Max(0, Plugin.Config.PaidSoulsJewelReserve.Value);
            if (balance + 0.0001 < cost + reserve) return false;
            if (!PaidSoulUspCostRatioReady(out _paidPlateauSummary))
            {
                if (Time.unscaledTime >= _paidNextPlateauLogAt)
                {
                    _paidNextPlateauLogAt = Time.unscaledTime + 300f;
                    SmartLog.Info(SmartLogCategory.Endgame,
                        $"Paid Souls strategy deferred: the balance covers the {tier}x tier and reserve, but the next-USP ratio is below the minimum. " +
                        "Quest and material strategies continue without spending Jewels of Soul.");
                }
                return false;
            }
            if (!PaidSoulsEquipmentProgressReady(out _paidEquipmentSummary))
            {
                if (Time.unscaledTime >= _paidNextEquipmentLogAt)
                {
                    _paidNextEquipmentLogAt = Time.unscaledTime + 120f;
                    SmartLog.Info(SmartLogCategory.Endgame,
                        "Paid Souls strategy deferred: the USP ratio is eligible, but this Ascension's highest equipment is not near its current economic limit. " +
                        "Normal upgrades, quests, and material strategies continue.");
                }
                return false;
            }
            var existingRage = RageModeManager.instance;
            if ((ReadSoulsEvent().Active && !_permanentFreeSoulsEventOwned) || ReadHordeActive() ||
                existingRage != null && existingRage.currentState != RageModeManager.RageModeStates.NotActive)
                return false; // Finish the current free value window before entering long exclusive preparation.

            var requestedPills = RequiredPaidRagePillUses(effect);
            var supply = _materials.FindDedicatedCraftSupplyNeed(SmartItem.RagePill, requestedPills,
                $"{tier}x paid Souls Rage Pill reserve", out var targetUses, out _, out var unavailable);
            if (targetUses <= 0 || unavailable != null)
            {
                LogPaidUnavailable(unavailable ?? "unable to build a Rage Pill supply plan");
                _paidNextEligibilityScan = Time.unscaledTime + 30f;
                return false;
            }

            BeginPaidSoulsPreparation(purchase, effect, tier, cost, targetUses, supply, balance);
        }

        if (!PaidSoulsBurstOwnsControl) return false;

        if (_paidSoulsPhase is PaidSoulsBurstPhase.PurchaseRequested or
            PaidSoulsBurstPhase.ChestHunt or PaidSoulsBurstPhase.Bursting)
        {
            if (_paidWaitForChestCompletion && ChestHuntInProgress())
                _paidChestObservedAfterPurchase = true;

            if (effectActive)
            {
                _paidEffectMissingSince = -1f;
                if (!_paidPurchaseConfirmed)
                {
                    _paidPurchaseConfirmed = true;
                    _paidPurchaseFailures = 0;
                    _pillAttemptedThisRage = false;
                    SmartLog.Info(SmartLogCategory.Endgame,
                        $"Paid Souls purchase confirmed by the live effect: multiplier={paidMultiplier}x, remaining={paidSecondsLeft:0.0}s, " +
                        $"Jewels {_paidBalanceBeforePurchase:0} -> {CurrentJewelsOfSoul():0}. Rage chaining begins after Chest Hunt ends.");
                }

                if (_paidWaitForChestCompletion)
                {
                    if (ChestHuntInProgress())
                    {
                        _paidSoulsPhase = PaidSoulsBurstPhase.ChestHunt;
                        return true;
                    }

                // Purchase occurs in the key warning range. Observe Chest Hunt actually start and then exit before
                // crafting the first pill or activating Rage. If the key is missed, skip the pill rather than consume
                // materials before the minigame.
                    if (!_paidChestObservedAfterPurchase)
                    {
                        _paidSoulsPhase = PaidSoulsBurstPhase.PurchaseRequested;
                        return true;
                    }

                    _paidWaitForChestCompletion = false;
                    _paidSoulsPhase = PaidSoulsBurstPhase.Bursting;
                    SmartLog.Info(SmartLogCategory.Endgame,
                        "Chest Hunt ended; Rage Pills and paid-Souls Rage chaining are now allowed. AutoProgression Ascension protection remains active until the bonus ends.");
                }

                _paidSoulsPhase = PaidSoulsBurstPhase.Bursting;
                if (!stable) return true;
                HandlePaidSoulsRageBurst(paidSecondsLeft, paidMultiplier);
                return true;
            }

            if (_paidPurchaseConfirmed)
            {
                // Chest Hunt or Bonus Stage transitions may temporarily hide components while relevant timers pause.
                // Accumulate effect-loss confirmation only after stable running resumes, avoiding a false expiry.
                if (!stable || ChestHuntInProgress())
                {
                    _paidEffectMissingSince = -1f;
                    return true;
                }
                if (_paidEffectMissingSince < 0)
                {
                    _paidEffectMissingSince = Time.unscaledTime;
                    return true;
                }
                var disappearanceConfirmation = Math.Max(5f,
                    Plugin.Config.PaidSoulsPurchaseConfirmationSeconds.Value);
                if (Time.unscaledTime - _paidEffectMissingSince < disappearanceConfirmation)
                    return true;
                FinishPaidSoulsBurst($"the {_paidTierMultiplier}x paid Souls effect ended");
                return false;
            }

            if (ChestHuntInProgress())
            {
                _paidSoulsPhase = PaidSoulsBurstPhase.ChestHunt;
                return true;
            }

            var confirmSeconds = Math.Max(0.5f, Plugin.Config.PaidSoulsPurchaseConfirmationSeconds.Value);
            if (Time.unscaledTime - _paidPurchaseRequestedAt < confirmSeconds) return true;
            _paidPurchaseFailures++;
            var spent = _paidBalanceBeforePurchase - CurrentJewelsOfSoul();
            if (spent > 0.5)
            {
                _paidEligibilityBlockedUntil = Time.unscaledTime + 3600f;
                SmartLog.Warning(SmartLogCategory.Endgame,
                    $"The paid Souls effect was not observed, but the balance decreased by {spent:0.#}; aborting this run to prevent another charge.");
                FinishPaidSoulsBurst("effect confirmation failed after the balance decreased");
                return false;
            }
            if (_paidPurchaseFailures >= 3)
            {
                _paidEligibilityBlockedUntil = Time.unscaledTime + 1800f;
                SmartLog.Warning(SmartLogCategory.Endgame,
                    "The paid Souls purchase was not confirmed after three attempts; releasing exclusive control and retrying later.");
                FinishPaidSoulsBurst("purchase confirmation failed repeatedly");
                return false;
            }

            _paidEligibilityBlockedUntil = Time.unscaledTime + 300f;
            SmartLog.Warning(SmartLogCategory.Endgame,
                $"The {_paidTierMultiplier}x purchase was neither confirmed nor charged. It will not be called again during this Chest Hunt; " +
                $"normal strategy resumes and eligibility will be reviewed in 5 minutes (failure {_paidPurchaseFailures}/3).");
            FinishPaidSoulsBurst("the purchase was neither charged nor activated; entering a short backoff");
            return false;
        }

        if (!stable || ChestHuntInProgress()) return true;
        if (!RevalidatePaidTierBudget())
        {
            FinishPaidSoulsBurst("USP ratio, equipment progress, Jewel balance, or tier cost is no longer eligible");
            return false;
        }

        if (Time.unscaledTime >= _paidNextSupplyScan &&
            (_paidSupplyNeed == null || PaidMaterialNeedSatisfied(_paidSupplyNeed) ||
             Time.unscaledTime >= _paidNextSupplyDeepScan))
        {
            _paidNextSupplyScan = Time.unscaledTime + 2f;
            _paidNextSupplyDeepScan = Time.unscaledTime + 30f;
            _paidTargetRagePillUses = RequiredPaidRagePillUses(_paidSoulsEffect);
            _paidSupplyNeed = _materials.FindDedicatedCraftSupplyNeed(SmartItem.RagePill,
                _paidTargetRagePillUses, $"{_paidTierMultiplier}x paid Souls Rage Pill reserve",
                out var cappedUses, out var ready, out var unavailable);
            if (cappedUses <= 0 || unavailable != null)
            {
                LogPaidUnavailable(unavailable ?? "the Rage Pill supply plan became invalid");
                FinishPaidSoulsBurst("Rage Pill materials cannot currently be restocked automatically");
                return false;
            }
            _paidTargetRagePillUses = cappedUses;
            if (ready && CurrentRagePillCraftableUses() + 0.0001 < cappedUses)
            {
                LogPaidUnavailable($"recipes appear stocked, but the game allows only {CurrentRagePillCraftableUses():0}/{cappedUses} Rage Pill uses");
                return true;
            }
        }

        if (_paidSupplyNeed != null)
        {
            _paidSoulsPhase = PaidSoulsBurstPhase.PreparingMaterials;
            _materialNeed = _paidSupplyNeed;
            HandlePaidSoulsMaterialRecovery(_paidSupplyNeed);
            return true;
        }

        _materialNeed = null;
        if (Time.unscaledTime >= _paidNextSoulMapScan || _paidTargetSoulMap?.Map == null)
        {
            _paidNextSoulMapScan = Time.unscaledTime +
                Math.Max(30f, Plugin.Config.MaterialDeepReplanIntervalSeconds.Value);
            _paidTargetSoulMap = MapEfficiencyEstimator.BestForSouls();
        }
        if (_paidTargetSoulMap?.Map == null)
        {
            LogPaidUnavailable("no unlocked and automatically farmable Souls map was found");
            return true;
        }

        var targetMap = _paidTargetSoulMap.Map;
        if (!MapEfficiencyEstimator.SameMap(targetMap, MapController.instance?.selectedMap))
        {
            _paidSoulsPhase = PaidSoulsBurstPhase.TravelingToSoulMap;
            _paidLastFocus = null;
            TryOpenQuestPortal(targetMap,
                $"{_paidTierMultiplier}x burst waiting point; corrected Souls/pattern={_paidTargetSoulMap.SoulsPerPattern:0.###E+0}",
                logType: "paid burst");
            return true;
        }

        _paidSoulsPhase = PaidSoulsBurstPhase.WaitingForChestHunt;
        var focus = targetMap.id ?? targetMap.name;
        if (!string.Equals(_paidLastFocus, focus, StringComparison.Ordinal))
        {
            _paidLastFocus = focus;
            SmartLog.Info(SmartLogCategory.Endgame,
                $"Paid Souls strategy ready: Rage Pill craftable uses={CurrentRagePillCraftableUses():0}/{_paidTargetRagePillUses}; " +
                $"target map={targetMap.name}, corrected Souls/pattern={_paidTargetSoulMap.SoulsPerPattern:0.###E+0}. " +
                "Quests, restocking, and travel remain paused while waiting for Chest Hunt.");
        }
        TryActivateReadyRage("paid Souls strategy waiting for Chest Hunt; natural cooldown only", force: true);
        return true;
    }

    internal bool PreparePaidSoulsPurchaseForChestHunt()
    {
        if (!Plugin.Config.Enabled.Value || _emergencyStopped ||
            !Plugin.Config.PaidSoulsChestBurstEnabled.Value ||
            _paidSoulsPhase != PaidSoulsBurstPhase.WaitingForChestHunt) return false;

        try
        {
            if (TryReadPaidSoulsEffect(out var existingSeconds, out var existingMultiplier))
            {
                BeginAdoptedPaidSoulsBurst(existingSeconds, existingMultiplier);
                return false;
            }
            if (!Plugin.Config.AutomaticMaterialTravel.Value || !Plugin.Config.RagePillEnabled.Value)
            {
                SmartLog.Info(SmartLogCategory.Endgame,
                    "Skipped the paid Souls purchase because material travel or Rage Pills were disabled before Chest Hunt; no Jewels were spent.");
                FinishPaidSoulsBurst("material travel or Rage Pills were disabled during preparation");
                return false;
            }
            RefreshPaidSoulsUspCostRatio();
            if (!PaidSoulUspCostRatioReady(out _paidPlateauSummary))
            {
                SmartLog.Info(SmartLogCategory.Endgame,
                    "Skipped the paid Souls purchase because the USP ratio was no longer eligible when Chest Hunt started.");
                FinishPaidSoulsBurst("the USP ratio became ineligible before Chest Hunt");
                return false;
            }
            if (!PaidSoulsEquipmentProgressReady(out _paidEquipmentSummary))
            {
                SmartLog.Info(SmartLogCategory.Endgame,
                    "Skipped the paid Souls purchase because this Ascension's highest equipment was still below the required progress.");
                FinishPaidSoulsBurst("highest equipment progress was insufficient before Chest Hunt");
                return false;
            }
        // Chest Hunt start is the final authoritative boundary before a charge; resolve the live highest tier again.
            if (!TryResolveHighestPaidSoulsTier(out var purchase, out var effect, out var tier, out var cost,
                    forceScan: true) ||
                !PaidSoulsTierAccepted(tier))
            {
                SmartLog.Warning(SmartLogCategory.Endgame,
                    "Skipped the paid Souls purchase because the purchase or live-effect component was unavailable when Chest Hunt started.");
                return false;
            }
            _paidTargetSoulMap = MapEfficiencyEstimator.BestForSouls();
            if (_paidTargetSoulMap?.Map == null || MapController.instance?.changingMap == true ||
                !MapEfficiencyEstimator.SameMap(_paidTargetSoulMap.Map, MapController.instance?.selectedMap))
            {
                SmartLog.Info(SmartLogCategory.Endgame,
                    "Skipped the paid Souls purchase because the highest-Souls map was not stable when Chest Hunt started; no Jewels were spent.");
                return false;
            }

            _paidTargetRagePillUses = RequiredPaidRagePillUses(effect);
            var supply = _materials.FindDedicatedCraftSupplyNeed(SmartItem.RagePill,
                _paidTargetRagePillUses, $"{tier}x paid Souls Rage Pill reserve",
                out var cappedUses, out var ready, out var unavailable);
            if (!ready || supply != null || unavailable != null || cappedUses <= 0 ||
                CurrentRagePillCraftableUses() + 0.0001 < cappedUses)
            {
                _paidSupplyNeed = supply;
                _paidNextSupplyScan = 0;
                SmartLog.Info(SmartLogCategory.Endgame,
                    $"Skipped the paid Souls purchase because Rage Pill stock was consumed or incomplete " +
                    $"({CurrentRagePillCraftableUses():0}/{Math.Max(1, cappedUses)} uses); restocking continues after Chest Hunt.");
                return false;
            }

            var reserve = Math.Max(0, Plugin.Config.PaidSoulsJewelReserve.Value);
            var balance = CurrentJewelsOfSoul();
            if (balance + 0.0001 < cost + reserve)
            {
                SmartLog.Info(SmartLogCategory.Endgame,
                    $"Skipped the paid Souls purchase: Jewels={balance:0} < cost={cost:0} + reserve={reserve}.");
                return false;
            }

            _paidSoulsPurchase = purchase;
            _paidSoulsEffect = effect;
            _paidTierMultiplier = tier;
            _paidTierCost = cost;
            _paidBalanceBeforePurchase = balance;
            _paidPurchaseRequestedAt = Time.unscaledTime;
            _paidPurchaseConfirmed = false;
            _paidEffectMissingSince = -1f;
            _paidWaitForChestCompletion = true;
            _paidChestObservedAfterPurchase = ChestHuntInProgress();
            _paidSoulsPhase = PaidSoulsBurstPhase.PurchaseRequested;
            purchase._PurchaseHandler_b__10_0();
            SmartLog.Info(SmartLogCategory.Endgame,
                $"Called the native {tier}x Souls purchase entry at the Chest Hunt key pre-trigger: cost={cost:0}, " +
                $"balance before purchase={balance:0}, reserve={reserve}. Waiting for live-effect confirmation without repeating this call.");
            return true;
        }
        catch (Exception ex)
        {
            _paidSoulsPhase = PaidSoulsBurstPhase.WaitingForChestHunt;
            _paidPurchaseRequestedAt = Time.unscaledTime;
            SmartLog.Warning(SmartLogCategory.Endgame,
                $"Paid Souls purchase call failed; Chest Hunt was allowed and the call will not repeat during this event: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void BeginPaidSoulsPreparation(JewelsOfSoulSoulsBonus purchase, PaidSoulsBonus effect,
        int tier, double cost, int targetPills, MaterialNeed supply, double balance)
    {
        _paidSoulsPurchase = purchase;
        _paidSoulsEffect = effect;
        _paidTierMultiplier = tier;
        _paidTierCost = cost;
        _paidTargetRagePillUses = targetPills;
        _paidSupplyNeed = supply;
        _paidTargetSoulMap = null;
        _paidSoulsPhase = supply == null
            ? PaidSoulsBurstPhase.TravelingToSoulMap
            : PaidSoulsBurstPhase.PreparingMaterials;
        _paidNextSupplyScan = 0;
        _paidNextSupplyDeepScan = Time.unscaledTime + 30f;
        _paidNextSoulMapScan = 0;
        _paidLastFocus = null;
        _paidPurchaseConfirmed = false;
        _paidEffectMissingSince = -1f;
        _paidRagePillsUsed = 0;
        _paidWaitForChestCompletion = false;
        _paidChestObservedAfterPurchase = false;
        _quests?.ReleasePlan("exclusive paid Souls preparation");
        _questPlan = null;
        _parallelQuestPlans = Array.Empty<QuestPlan>();
        _materialNeed = supply;
        ClearMaterialLinger();
        var testTier = PaidSoulsTestTier();
        if (testTier > 0)
            SmartLog.Warning(SmartLogCategory.Endgame,
                $"Paid Souls test tier is enabled: only the exact {testTier}x tier may be purchased. Restore the setting to 0 after testing.");
        SmartLog.Info(SmartLogCategory.Endgame,
            $"Entered exclusive paid Souls strategy: Jewels={balance:0}, tier={tier}x, cost={cost:0}, " +
            $"post-purchase reserve={Math.Max(0, Plugin.Config.PaidSoulsJewelReserve.Value)}, target Rage Pill uses={targetPills}. " +
            "Normal quests, rerolls, surplus restocking, and quest travel are paused until the paid bonus ends.");
    }

    private void BeginAdoptedPaidSoulsBurst(double secondsLeft, int multiplier)
    {
        _paidSoulsPhase = PaidSoulsBurstPhase.Bursting;
        _paidPurchaseConfirmed = true;
        _paidPurchaseFailures = 0;
        _paidEffectMissingSince = -1f;
        _paidWaitForChestCompletion = false;
        _paidChestObservedAfterPurchase = true;
        _pillAttemptedThisRage = false;
        _paidTierMultiplier = multiplier;
        _paidRagePillsUsed = 0;
        _paidTargetSoulMap = null;
        _paidSupplyNeed = null;
        _materialNeed = null;
        _quests?.ReleasePlan("active paid Souls effect detected; taking control of the remaining window");
        _questPlan = null;
        _parallelQuestPlans = Array.Empty<QuestPlan>();
        ClearMaterialLinger();
        SmartLog.Info(SmartLogCategory.Endgame,
            $"Detected an active paid Souls effect: multiplier={multiplier}x, remaining={secondsLeft:0.0}s. " +
            "Other strategies are paused and no mid-window travel will occur.");
    }

    private void HandlePaidSoulsMaterialRecovery(MaterialNeed need)
    {
        if (need?.Material == null || need.Map == null) return;
        var materialName = DisplayName(need.Material);
        if (MapEfficiencyEstimator.SameMap(need.Map, MapController.instance?.selectedMap))
        {
            var focus = $"paid-material|{need.Material.GetInstanceID()}|{need.Map.id ?? need.Map.name}";
            if (!string.Equals(_paidLastFocus, focus, StringComparison.Ordinal))
            {
                _paidLastFocus = focus;
                SmartLog.Info(SmartLogCategory.Endgame,
                    $"Paid Souls restocking: map={need.Map.name}, material={need.Material?.name ?? "unknown"}, " +
                    $"amount={need.Current:0}/{need.Desired:0}, target Rage Pill uses={_paidTargetRagePillUses}. " +
                    "The full batch will be stocked before traveling to the highest-Souls map.");
            }
            if (need.GiantDrop && Plugin.Config.ShardsNecklaceEnabled.Value &&
                CanCraftWithoutReducingPaidPillStock(SmartItem.ShardsNecklace) &&
                NeedsCoverage(SmartItem.ShardsNecklace, 20) &&
                TryCraft(SmartItem.ShardsNecklace, Plugin.Config.ShardsNecklaceReserveUses.Value,
                    $"paid burst Giant material {materialName}", allowReserveBorrow: true)) return;
            if (Plugin.Config.WhetstoneDuringMaterialRecovery.Value &&
                need.ExpectedEligibleKills >= Math.Max(1, Plugin.Config.WhetstoneMinimumExpectedEligibleKills.Value) &&
                CanCraftWithoutReducingPaidPillStock(SmartItem.Whetstone) &&
                NeedsCoverage(SmartItem.Whetstone, 20) &&
                TryCraft(SmartItem.Whetstone, Plugin.Config.WhetstoneReserveUses.Value,
                    $"paid burst supply {materialName}", allowReserveBorrow: true)) return;
            TryActivateReadyRage($"paid burst Rage Pill material {materialName}; natural cooldown only", force: true);
            return;
        }

        _paidLastFocus = null;
        TryOpenQuestPortal(need.Map,
            $"restock {materialName} for the {_paidTierMultiplier}x burst first: {need.Current:0}/{need.Desired:0}",
            logType: "paid burst supply");
    }

    private void HandlePaidSoulsRageBurst(double paidSecondsLeft, int paidMultiplier)
    {
        var rage = RageModeManager.instance;
        if (rage == null || paidSecondsLeft <= 0) return;
        var minimumStart = Math.Max(1, Plugin.Config.MinimumSecondsToStartRage.Value);

        if (rage.currentState != RageModeManager.RageModeStates.NotActive)
        {
            var currentRageCoverage = Math.Max(0, EstimatedRageSecondsRemaining);
            if (Plugin.Config.RagePillEnabled.Value && rage.currentCd > 0.01 && !_pillAttemptedThisRage &&
                paidSecondsLeft >= currentRageCoverage + minimumStart)
            {
                _pillAttemptedThisRage = true;
                var before = rage.currentCd;
                if (TryCraft(SmartItem.RagePill, 0,
                    $"paid Souls {paidMultiplier}x Rage-chain cooldown preparation", allowReserveBorrow: true))
                {
                    _paidRagePillsUsed++;
                    SmartLog.Info(SmartLogCategory.Endgame,
                        $"Paid Souls Rage sustain used a pill to clear cooldown {before:0.0}s -> {rage.currentCd:0.0}s; " +
                        $"bonus remaining={paidSecondsLeft:0.0}s, pills used={_paidRagePillsUsed}.");
                }
            }
            return;
        }

        if (paidSecondsLeft < minimumStart) return;
        if (rage.currentCd > 0.01)
        {
        // Disabling pills after the paid multiplier starts does not abandon the whole window. Stop material spending,
        // but continue activating Rage when natural cooldown reaches zero.
            if (!Plugin.Config.RagePillEnabled.Value) return;
            var before = rage.currentCd;
            if (TryCraft(SmartItem.RagePill, 0,
                $"resume Rage immediately for paid Souls {paidMultiplier}x", allowReserveBorrow: true))
            {
                _paidRagePillsUsed++;
                SmartLog.Info(SmartLogCategory.Endgame,
                    $"Paid Souls Rage startup used a pill to clear cooldown {before:0.0}s -> {rage.currentCd:0.0}s; " +
                    $"bonus remaining={paidSecondsLeft:0.0}s, pills used={_paidRagePillsUsed}.");
            }
            return;
        }

        TryActivateReadyRage($"paid Souls {paidMultiplier}x with {paidSecondsLeft:0.0}s remaining", force: true);
    }

    private bool RevalidatePaidTierBudget()
    {
        if (!Plugin.Config.AutomaticMaterialTravel.Value || !Plugin.Config.RagePillEnabled.Value)
            return false;
        RefreshPaidSoulsUspCostRatio();
        if (!PaidSoulUspCostRatioReady(out _paidPlateauSummary)) return false;
        if (!PaidSoulsEquipmentProgressReady(out _paidEquipmentSummary)) return false;
        if (!TryResolveHighestPaidSoulsTier(out var purchase, out var effect, out var tier, out var cost))
            return false;
        if (!PaidSoulsTierAccepted(tier)) return false;
        var balance = CurrentJewelsOfSoul();
        if (balance + 0.0001 < cost + Math.Max(0, Plugin.Config.PaidSoulsJewelReserve.Value)) return false;
        _paidSoulsPurchase = purchase;
        _paidSoulsEffect = effect;
        _paidTierMultiplier = tier;
        _paidTierCost = cost;
        return true;
    }

    private bool TryResolveHighestPaidSoulsTier(out JewelsOfSoulSoulsBonus purchase,
        out PaidSoulsBonus effect, out int multiplier, out double cost, bool forceScan = false)
    {
        purchase = FindLoadedSceneComponent(_paidSoulsPurchase);
        effect = FindLoadedSceneComponent(_paidSoulsEffect);
        multiplier = purchase?.multiplier ?? 0;
        cost = purchase?.button?.cost ?? 0;
        try
        {
            if (!forceScan && purchase != null && effect != null && PaidSoulsTierAccepted(multiplier) &&
                double.IsFinite(cost) && cost > 0) return true;
            purchase = null;
            multiplier = 0;
            cost = 0;
            var testTier = PaidSoulsTestTier();
            foreach (var candidate in Resources.FindObjectsOfTypeAll<JewelsOfSoulSoulsBonus>())
            {
                if (!IsLoadedSceneComponent(candidate) || candidate.button == null ||
                    !double.IsFinite(candidate.button.cost) ||
                    candidate.button.cost <= 0) continue;
                if (testTier > 0)
                {
                    if (candidate.multiplier != testTier) continue;
                    purchase = candidate;
                    multiplier = candidate.multiplier;
                    cost = candidate.button.cost;
                    break;
                }
                if (candidate.multiplier <= multiplier) continue;
                purchase = candidate;
                multiplier = candidate.multiplier;
                cost = candidate.button.cost;
            }
            effect ??= FindLoadedSceneComponent<PaidSoulsBonus>();
            if (purchase == null || effect == null)
            {
                LogPaidUnavailable($"paid Souls components are not ready: purchase={purchase != null}, live effect={effect != null}");
                return false;
            }
            _paidSoulsPurchase = purchase;
            _paidSoulsEffect = effect;
            return true;
        }
        catch (Exception ex)
        {
            LogPaidUnavailable($"unable to read the paid Souls tier: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool TryReadPaidSoulsEffect(out double secondsLeft, out int multiplier)
    {
        secondsLeft = 0;
        multiplier = 0;
        try
        {
            _paidSoulsEffect = FindLoadedSceneComponent(_paidSoulsEffect);
            if (_paidSoulsEffect == null)
            {
                if (!PaidSoulsBurstOwnsControl && Time.unscaledTime < _paidNextEffectResolveScan) return false;
                _paidNextEffectResolveScan = Time.unscaledTime + 5f;
                _paidSoulsEffect = FindLoadedSceneComponent<PaidSoulsBonus>();
            }
            if (_paidSoulsEffect == null || !_paidSoulsEffect.IsActive()) return false;
            secondsLeft = Math.Max(0, _paidSoulsEffect.timeLeft);
            multiplier = Math.Max(1, _paidSoulsEffect.soulsMultiplier);
            return secondsLeft > 0.01;
        }
        catch
        {
            _paidSoulsEffect = null;
            _paidNextEffectResolveScan = Time.unscaledTime + 5f;
            return false;
        }
    }

    private static T FindLoadedSceneComponent<T>() where T : Component
    {
        foreach (var component in Resources.FindObjectsOfTypeAll<T>())
            if (IsLoadedSceneComponent(component)) return component;
        return null;
    }

    private static T FindLoadedSceneComponent<T>(T component) where T : Component =>
        IsLoadedSceneComponent(component) ? component : null;

    private static bool IsLoadedSceneComponent(Component component)
    {
        if (component == null || component.gameObject == null) return false;
        var scene = component.gameObject.scene;
        return scene.IsValid() && scene.isLoaded;
    }

    private int RequiredPaidRagePillUses(PaidSoulsBonus effect)
    {
        try
        {
            var effectSeconds = effect?.duration ?? 240;
            var inventory = PlayerInventory.instance;
            if (inventory?.randomEventsTimeModifier != null)
                effectSeconds = Utils.GetMultiplicativeValue(effectSeconds,
                    inventory.randomEventsTimeModifier);
            if (effect?.durationModifier != null)
                effectSeconds = Utils.GetMultiplicativeValue(effectSeconds, effect.durationModifier);
            if (!double.IsFinite(effectSeconds) || effectSeconds <= 0) effectSeconds = 240;
            var rageSeconds = RageModeManager.instance?.GetDuration() ?? 20;
            if (!double.IsFinite(rageSeconds) || rageSeconds <= 0) rageSeconds = 20;
            return Math.Max(1, (int)Math.Min(int.MaxValue, Math.Ceiling(effectSeconds / rageSeconds)));
        }
        catch
        {
            return 12;
        }
    }

    private double CurrentRagePillCraftableUses()
    {
        try { return Math.Max(0, _catalog.Get(SmartItem.RagePill)?.HowManyCanCraft() ?? 0); }
        catch { return 0; }
    }

    private bool CanCraftWithoutReducingPaidPillStock(SmartItem candidateKind)
    {
        if (!PaidSoulsBurstOwnsControl || _paidSoulsPhase is PaidSoulsBurstPhase.PurchaseRequested or
            PaidSoulsBurstPhase.ChestHunt or PaidSoulsBurstPhase.Bursting ||
            _paidTargetRagePillUses <= 0 || candidateKind == SmartItem.RagePill) return true;
        var candidate = _catalog.Get(candidateKind);
        var pill = _catalog.Get(SmartItem.RagePill);
        if (candidate == null || pill == null) return true;
        try
        {
            var candidateRequirements = candidate.GetRequirements();
            var pillRequirements = pill.GetRequirements();
            if (candidateRequirements == null || pillRequirements == null) return true;
            for (var i = 0; i < pillRequirements.Length; i++)
            {
                var pillRequirement = pillRequirements[i];
                var material = pillRequirement?.material;
                if (material == null || pillRequirement.divinityPoints || pillRequirement.amount <= 0) continue;
                var sharedConsumption = 0.0;
                for (var j = 0; j < candidateRequirements.Length; j++)
                {
                    var candidateRequirement = candidateRequirements[j];
                    if (candidateRequirement?.material == null || candidateRequirement.divinityPoints ||
                        candidateRequirement.amount <= 0 ||
                        !SameDrop(candidateRequirement.material, material)) continue;
                    sharedConsumption += candidateRequirement.amount;
                }
                if (sharedConsumption <= 0) continue;
                var remaining = Math.Max(0, material.amount - sharedConsumption -
                    MaterialReservePolicy.MinimumReserve(material));
                var pillUsesAfter = Math.Floor(remaining / pillRequirement.amount);
                if (pillUsesAfter + 0.0001 >= _paidTargetRagePillUses) continue;
                LogCraftSkip(candidateKind, candidate,
                    $"paid Souls strategy reserves {_paidTargetRagePillUses} Rage Pill uses; shared material {DisplayName(material)} would leave only {Math.Max(0, pillUsesAfter):0} uses");
                return false;
            }
            return true;
        }
        catch
        {
            return false; // During reserved pre-purchase state, do not consume shared materials unless safety is proven.
        }
    }

    private static bool SameDrop(Drop left, Drop right)
    {
        if (left == null || right == null) return false;
        if (left == right) return true;
        var leftName = CraftableCatalog.Normalize(left.name);
        var rightName = CraftableCatalog.Normalize(right.name);
        return !string.IsNullOrEmpty(leftName) && leftName == rightName;
    }

    private static bool PaidMaterialNeedSatisfied(MaterialNeed need)
    {
        try { return need?.Material == null || need.Material.amount + 0.0001 >= need.Desired; }
        catch { return true; }
    }

    private static double CurrentJewelsOfSoul()
    {
        try
        {
            var value = PlayerInventory.instance?.jewelsOfSoul ?? 0;
            return double.IsFinite(value) ? Math.Max(0, value) : 0;
        }
        catch { return 0; }
    }

    private void RefreshPaidSoulsUspCostRatio()
    {
        try
        {
            var inventory = PlayerInventory.instance;
            if (inventory == null) return;
            _paidObservedUltraAscensions = Math.Max(0, inventory.ultraAscensions);
        // points is the unspent subset of lifetime and must not be added twice. The authoritative Current UA SP formula
        // used by the game is lifetime plus pre, where pre is currently unclaimed SP.
            var currentUaSp = SlayerPoints.lifetime + SlayerPoints.pre;
            var nextUsp = UltraSlayerPoints.slayerPointsForNextUSP;
            _paidCurrentUaSp = double.IsFinite(currentUaSp) ? Math.Max(0, currentUaSp) : 0;
            _paidSpForNextUsp = double.IsFinite(nextUsp) ? Math.Max(0, nextUsp) : 0;
            _paidNextUspCostRatioPercent = _paidCurrentUaSp > 0 && _paidSpForNextUsp > 0
                ? _paidSpForNextUsp / _paidCurrentUaSp * 100.0
                : double.PositiveInfinity;

            var minimumUltra = Math.Max(0, Plugin.Config.PaidSoulsMinimumUltraAscensions.Value);
            var threshold = PaidSoulsMinimumUspRemainingRatio();
            _paidPlateauSummary = _paidObservedUltraAscensions + 0.0001 < minimumUltra
                ? $"Ultra Ascensions={_paidObservedUltraAscensions:0}/{minimumUltra}"
                : _paidCurrentUaSp <= 0 || _paidSpForNextUsp <= 0
                    ? "Current UA SP or next-USP cost is not initialized"
                    : $"next USP requires {FormatProgressValue(_paidSpForNextUsp)} SP, Current UA SP={FormatProgressValue(_paidCurrentUaSp)}, remaining ratio={_paidNextUspCostRatioPercent:0.##}%, minimum={threshold:0.##}%";
        }
        catch
        {
            _paidCurrentUaSp = 0;
            _paidSpForNextUsp = 0;
            _paidNextUspCostRatioPercent = double.PositiveInfinity;
            _paidPlateauSummary = "unable to read next-USP cost or Current UA SP";
        }
    }

    private bool PaidSoulUspCostRatioReady(out string summary)
    {
        summary = _paidPlateauSummary;
        var minimumUltra = Math.Max(0, Plugin.Config.PaidSoulsMinimumUltraAscensions.Value);
        var ratioThreshold = PaidSoulsMinimumUspRemainingRatio();
        return double.IsFinite(_paidObservedUltraAscensions) &&
               _paidObservedUltraAscensions + 0.0001 >= minimumUltra &&
               _paidCurrentUaSp > 0 && _paidSpForNextUsp > 0 &&
               double.IsFinite(_paidNextUspCostRatioPercent) &&
               _paidNextUspCostRatioPercent + 0.0001 >= ratioThreshold;
    }

    private bool PaidSoulsEquipmentProgressReady(out string summary)
    {
        summary = "highest equipment data is not initialized for this Ascension";
        try
        {
            var inventory = PlayerInventory.instance;
            var powers = inventory?.powers;
            if (inventory == null || powers == null || powers.Length == 0) return false;

            Power highest = null;
            var coinRecord = Math.Max(0, inventory.currentAscensionCoinsRecord);
            for (var i = 0; i < powers.Length; i++)
            {
                var power = powers[i];
                if (power == null) continue;
                var prerequisite = power.lockedBehindUpgrade;
                if (prerequisite != null && !prerequisite.bought) continue;
            // Array order is the native shop order. Level above zero means the item is owned this Ascension; otherwise
            // the next item is economically reachable only after this Ascension's coin record reaches its first price.
                if (power.level <= 0 && coinRecord + 0.0001 < power.baseCost) continue;
                highest = power;
            }
            if (highest == null) return false;

            var minimumLevel = Math.Max(1, Plugin.Config.PaidSoulsHighestEquipmentMinimumLevel.Value);
            var maximumAffordable = Math.Max(0,
                Plugin.Config.PaidSoulsHighestEquipmentMaximumAffordableLevels.Value);
            var affordableLevels = 0;
            var maxPurchase = highest.CalculateMaxCost();
            var currentCoins = Math.Max(0, inventory.coins);
            if (maxPurchase.accumulated > 0 && maxPurchase.cost <= currentCoins + 0.0001)
                affordableLevels = maxPurchase.accumulated;

            var name = string.IsNullOrWhiteSpace(highest.localizedName) ? highest.name : highest.localizedName;
            summary = $"highest reachable equipment={name ?? "unknown"}, level={highest.level}/{minimumLevel}, " +
                      $"currently affordable levels={affordableLevels}/{maximumAffordable}";
            return highest.level >= minimumLevel && affordableLevels <= maximumAffordable;
        }
        catch (Exception ex)
        {
            summary = $"unable to read highest equipment progress: {ex.GetType().Name}";
            return false;
        }
    }

    private static double PaidSoulsMinimumUspRemainingRatio()
    {
        var configured = Plugin.Config.PaidSoulsMinimumNextUspRemainingRatioPercent.Value;
        return float.IsFinite(configured) ? Math.Clamp(configured, 0.01f, 100f) : 5;
    }

    private static int PaidSoulsTestTier() =>
        Math.Max(0, Plugin.Config.PaidSoulsTestTierMultiplier.Value);

    private static bool PaidSoulsTierAccepted(int tier)
    {
        var testTier = PaidSoulsTestTier();
        return testTier > 0
            ? tier == testTier
            : tier >= Math.Max(1, Plugin.Config.PaidSoulsMinimumTierMultiplier.Value);
    }

    private static string FormatProgressValue(double value)
    {
        if (!double.IsFinite(value)) return "∞";
        if (value <= 0) return "0";
        try { return AbbreviationUtility.AbbreviateNumber(value, withNobr: false, fromValue: 1000); }
        catch { return value >= 1000 ? $"{value:0.###E+0}" : $"{value:0.##}"; }
    }

    private static bool ChestHuntInProgress()
    {
        try { return GameState.current == GameStates.ChestHunt || ChestHuntManager.instance?.eventInProgress == true; }
        catch { return GameState.current == GameStates.ChestHunt; }
    }

    private void FinishPaidSoulsBurst(string reason)
    {
        if (!PaidSoulsBurstOwnsControl) return;
        var previous = _paidSoulsPhase;
        _paidSoulsPhase = PaidSoulsBurstPhase.Idle;
        _paidSupplyNeed = null;
        _paidTargetSoulMap = null;
        _paidSoulsPurchase = null;
        _paidTargetRagePillUses = 0;
        _paidTierMultiplier = 0;
        _paidTierCost = 0;
        _paidPurchaseConfirmed = false;
        _paidWaitForChestCompletion = false;
        _paidChestObservedAfterPurchase = false;
        _paidLastFocus = null;
        _materialNeed = null;
        _paidNextEligibilityScan = Time.unscaledTime + 5f;
        ClearMaterialLinger();
        RequestImmediateRoutingReplan($"paid Souls strategy ended: {reason}");
        SmartLog.Info(SmartLogCategory.Endgame,
            $"Exclusive paid Souls strategy ended at phase={previous}; reason={reason}. " +
            "Released Rage Pill reserves and the highest-Souls map anchor; quests, rerolls, materials, and travel resumed.");
    }

    private void LogPaidUnavailable(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || Time.unscaledTime < _paidNextUnavailableLogAt) return;
        _paidNextUnavailableLogAt = Time.unscaledTime + 30f;
        SmartLog.Debug(SmartLogCategory.Endgame, $"Paid Souls strategy is not ready: {reason}.");
    }

    private string PaidSoulsBurstStatusSummary()
    {
        if (Plugin.EndgameModeActive && Plugin.Config.EndgamePermanentPaidBonusesEnabled.Value)
            return EndgameModeStatusSummary();
        if (!PaidSoulsBurstOwnsControl)
        {
            RefreshPaidSoulsUspCostRatio();
            PaidSoulUspCostRatioReady(out _paidPlateauSummary);
            PaidSoulsEquipmentProgressReady(out _paidEquipmentSummary);
            var tierMode = PaidSoulsTestTier() > 0 ? $"test tier={PaidSoulsTestTier()}x" : "tier=automatic highest";
            return $"Paid Souls burst: idle, {tierMode}, Jewels={CurrentJewelsOfSoul():0}, " +
                   $"reserve={Math.Max(0, Plugin.Config.PaidSoulsJewelReserve.Value)}, {_paidPlateauSummary}; {_paidEquipmentSummary}";
        }
        TryReadPaidSoulsEffect(out var seconds, out var multiplier);
        var chestGate = _paidWaitForChestCompletion
            ? _paidChestObservedAfterPurchase ? "entered, waiting for exit" : "waiting for entry"
            : "burst released";
        return $"Paid Souls burst: phase={_paidSoulsPhase}, tier={Math.Max(multiplier, _paidTierMultiplier)}x/{_paidTierCost:0}, " +
               $"Rage Pill uses={CurrentRagePillCraftableUses():0}/{_paidTargetRagePillUses}, Chest Hunt gate={chestGate}, " +
               $"highest-Souls map={_paidTargetSoulMap?.Map?.name ?? "not locked"}, paid time remaining={seconds:0.0}s";
    }
}

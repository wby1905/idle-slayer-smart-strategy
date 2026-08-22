using Il2Cpp;
using UnityEngine;

namespace SmartStrategy;

public sealed partial class StrategyBehaviour
{
    private const int EndgamePaidMultiplier = 500;

    private float _nextEndgamePaidBonusAttemptAt;
    private bool _endgamePaidBonusLogged;
    private float _nextEndgamePaidBonusErrorLogAt;

    private void EnsureEndgamePermanentPaidBonuses(bool stable)
    {
        if (!Plugin.EndgameModeActive || !Plugin.Config.EndgamePermanentPaidBonusesEnabled.Value || !stable ||
            Time.unscaledTime < _nextEndgamePaidBonusAttemptAt) return;
        _nextEndgamePaidBonusAttemptAt = Time.unscaledTime + 5f;

        try
        {
            var manager = JewelsOfSoulManager.instance;
            if (manager == null) return;

            var souls = manager.paidSoulsBonus ?? FindLoadedSceneComponent<PaidSoulsBonus>();
            var cps = manager.paidCpSBonus ?? FindLoadedSceneComponent<PaidCpSBonus>();
            if (souls == null || cps == null) return;
            manager.paidSoulsBonus ??= souls;
            manager.paidCpSBonus ??= cps;

            if (!souls.IsActive() || souls.soulsMultiplier != EndgamePaidMultiplier)
                manager.SoulsBonusBought(EndgamePaidMultiplier);
            if (!cps.IsActive() || Math.Abs(cps.cpsMultiplier - EndgamePaidMultiplier) > 0.001)
                manager.CPSBonusBought(EndgamePaidMultiplier);

            souls = manager.paidSoulsBonus ?? souls;
            cps = manager.paidCpSBonus ?? cps;
            if (souls.IsActive() && souls.soulsMultiplier == EndgamePaidMultiplier)
                PermanentClientBonuses.ManageRandomEvent(souls);
            if (cps.IsActive() && Math.Abs(cps.cpsMultiplier - EndgamePaidMultiplier) <= 0.001)
                PermanentClientBonuses.ManageRandomEvent(cps);

            if (!_endgamePaidBonusLogged && souls.IsActive() && cps.IsActive())
            {
                _endgamePaidBonusLogged = true;
                SmartLog.Info(SmartLogCategory.Endgame,
                    "Endgame mode activated and froze the 500x paid Souls and CpS timers.");
            }
        }
        catch (Exception ex)
        {
            _nextEndgamePaidBonusAttemptAt = Time.unscaledTime + 10f;
            if (Time.unscaledTime >= _nextEndgamePaidBonusErrorLogAt)
            {
                _nextEndgamePaidBonusErrorLogAt = Time.unscaledTime + 30f;
                SmartLog.Warning(SmartLogCategory.Endgame,
                    $"Unable to activate the endgame 500x bonuses; the operation will retry later: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static string EndgameModeStatusSummary()
    {
        try
        {
            var manager = JewelsOfSoulManager.instance;
            var souls = manager?.paidSoulsBonus;
            var cps = manager?.paidCpSBonus;
            var soulsStatus = souls?.IsActive() == true
                ? $"{souls.soulsMultiplier}x/{souls.timeLeft:0.0}s frozen"
                : "inactive";
            var cpsStatus = cps?.IsActive() == true
                ? $"{cps.cpsMultiplier:0}x/{cps.timeLeft:0.0}s frozen"
                : "inactive";
            return $"Endgame bonuses: paid Souls={soulsStatus}, paid CpS={cpsStatus}, Jewels={CurrentJewelsOfSoul():0.##}";
        }
        catch { return "Endgame bonuses: paid-bonus components are not ready"; }
    }
}

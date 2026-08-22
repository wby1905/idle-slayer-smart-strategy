using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace SmartStrategy;

/// <summary>
/// Learns the current save's real giant cadence. Samples are normalized back to the no-temporary-
/// necklace interval, so observations made during Shards Necklace remain useful after it expires.
/// </summary>
[HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.EnemySlayed),
    typeof(EnemyGameObject), typeof(EnemyDeathCause))]
internal static class GiantKillTimingBridge
{
    private static float _lastKillAt = -1f;
    private static string _lastMapId;
    private static double _lastSpawnFactor = 1;
    private static double _observedBaseIntervalSeconds;
    private static int _samples;
    private static float _nextDebugLogAt;

    internal static int Samples => _samples;

    internal static double BaseIntervalSeconds
    {
        get
        {
            var fallback = Math.Clamp(Plugin.Config?.ExpectedBaseGiantIntervalSeconds.Value ?? 85f, 20f, 300f);
            return _samples > 0 && double.IsFinite(_observedBaseIntervalSeconds)
                ? Math.Clamp(_observedBaseIntervalSeconds, 20, 300)
                : fallback;
        }
    }

    private static void Prefix(EnemyGameObject enemyGO)
    {
        try
        {
            var enemy = MapEfficiencyEstimator.CurrentStage(enemyGO?.enemyStats?.enemy);
            var map = MapController.instance?.selectedMap;
            if (enemy?.isGiant != true || !QuestDirector.IsStandardAvailableMap(map) ||
                GameState.current is not (GameStates.RunnerMode or GameStates.RageMode)) return;

            var now = Time.unscaledTime;
            var mapId = map.id ?? map.name ?? string.Empty;
            var factor = MapEfficiencyEstimator.CurrentGiantSpawnFactor(includeTemporaryShards: true);
            var contextChanged = !string.Equals(mapId, _lastMapId, StringComparison.Ordinal) ||
                                 Math.Abs(factor - _lastSpawnFactor) / Math.Max(1, _lastSpawnFactor) > 0.05;
            if (!contextChanged && _lastKillAt >= 0)
            {
                var interval = now - _lastKillAt;
                var normalizedBase = interval * factor;
                // Shorter samples are forced spawns/scene races; longer samples include paused minigames.
                if (interval >= 5 && interval <= 300 && normalizedBase >= 20 && normalizedBase <= 300)
                {
                    _observedBaseIntervalSeconds = _samples == 0
                        ? normalizedBase
                        : _observedBaseIntervalSeconds * 0.75 + normalizedBase * 0.25;
                    _samples++;
        // Persist the measured no-necklace baseline so the next run inherits history instead of cold-starting
        // from 85 seconds; the current session continues updating the higher-frequency in-memory EWMA.
                    if (Plugin.Config != null)
                        Plugin.Config.ExpectedBaseGiantIntervalSeconds.Value = (float)BaseIntervalSeconds;
                    if (now >= _nextDebugLogAt)
                    {
                        _nextDebugLogAt = now + 30f;
                        SmartLog.Debug(SmartLogCategory.Planning,
                            $"Giant timing calibration: observed interval={interval:0.0}s, spawn factor={factor:0.###}x, " +
                            $"normalized no-necklace baseline={BaseIntervalSeconds:0.0}s, samples={_samples}.");
                    }
                }
            }

            _lastKillAt = now;
            _lastMapId = mapId;
            _lastSpawnFactor = factor;
        }
        catch
        {
            // Timing calibration must never affect the game's kill path.
        }
    }
}

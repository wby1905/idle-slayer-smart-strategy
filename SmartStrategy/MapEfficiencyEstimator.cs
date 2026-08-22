using Il2Cpp;

namespace SmartStrategy;

internal sealed class MapEfficiency
{
    internal BaseMap Map { get; }
    internal double TargetEnemiesPerPattern { get; }
    internal double TotalEnemiesPerPattern { get; }
    internal int EligiblePatterns { get; }
    internal bool TargetsGiant { get; }
    internal double ExpectedEligibleKills { get; }

    internal MapEfficiency(BaseMap map, double targetEnemiesPerPattern, double totalEnemiesPerPattern, int eligiblePatterns,
        bool targetsGiant = false, double expectedEligibleKills = 0)
    {
        Map = map;
        TargetEnemiesPerPattern = targetEnemiesPerPattern;
        TotalEnemiesPerPattern = totalEnemiesPerPattern;
        EligiblePatterns = eligiblePatterns;
        TargetsGiant = targetsGiant;
        ExpectedEligibleKills = expectedEligibleKills;
    }
}

internal sealed class SoulMapEfficiency
{
    internal BaseMap Map { get; }
    internal double SoulsPerPattern { get; }
    internal int EligiblePatterns { get; }

    internal SoulMapEfficiency(BaseMap map, double soulsPerPattern, int eligiblePatterns)
    {
        Map = map;
        SoulsPerPattern = soulsPerPattern;
        EligiblePatterns = eligiblePatterns;
    }
}

/// <summary>
/// Reads the game's serialized enemy-pattern table at runtime. This makes map selection follow the
/// player's unlocked pattern level and the installed game version instead of a stale hard-coded wiki table.
/// </summary>
internal static class MapEfficiencyEstimator
{
    internal static SoulMapEfficiency BestForSouls()
    {
        var controller = MapController.instance;
        if (controller?.maps == null) return null;

        SoulMapEfficiency best = null;
        for (var i = 0; i < controller.maps.Length; i++)
        {
            var map = controller.maps[i];
            if (!QuestDirector.IsStandardAvailableMap(map)) continue;
            var estimate = EvaluateSouls(map);
            if (estimate.SoulsPerPattern <= 0) continue;
            if (best == null || IsMeaningfullyGreater(estimate.SoulsPerPattern, best.SoulsPerPattern) ||
                NearlyEqual(estimate.SoulsPerPattern, best.SoulsPerPattern) && SameMap(map, controller.selectedMap))
                best = estimate;
        }
        return best;
    }

    internal static MapEfficiency BestForQuest(Quest quest, double questCreditPerEnemy)
    {
        var controller = MapController.instance;
        if (quest == null || controller?.maps == null) return null;

        MapEfficiency best = null;
        var bestSeconds = double.MaxValue;
        var remaining = Math.Max(1, quest.questGoal - quest.questCurrentGoal);
        for (var i = 0; i < controller.maps.Length; i++)
        {
            var map = controller.maps[i];
            if (!QuestDirector.IsStandardAvailableMap(map)) continue;
            var estimate = EvaluateForQuest(map, quest);
            if (estimate.TargetEnemiesPerPattern <= 0) continue;
            var seconds = EstimatedWorkSeconds(estimate, remaining / Math.Max(1, questCreditPerEnemy));
            if (best == null || seconds < bestSeconds)
            {
                best = estimate;
                bestSeconds = seconds;
            }
        }
        return best;
    }

    internal static MapEfficiency BestForMaterial(Drop material, double materialsNeeded)
    {
        return BestForMaterial(MaterialProfiles(material), materialsNeeded);
    }

    internal static IReadOnlyList<MapEfficiency> MaterialProfiles(Drop material)
    {
        var controller = MapController.instance;
        if (material == null || controller?.maps == null) return Array.Empty<MapEfficiency>();

        var targetName = NormalizedName(material);
        if (string.IsNullOrEmpty(targetName)) return Array.Empty<MapEfficiency>();
        var profiles = new List<MapEfficiency>();
        for (var i = 0; i < controller.maps.Length; i++)
        {
            var map = controller.maps[i];
            if (!QuestDirector.IsStandardAvailableMap(map)) continue;
            var estimate = Evaluate(map, enemy =>
            {
                var current = CurrentStage(enemy);
                return CanAutomateEnemy(current) &&
                    EnemyDrops(current, targetName);
            }, allowMembershipFallback: true);
            if (estimate.TargetEnemiesPerPattern <= 0) continue;
            var giantDrop = MapTargetDropsFromGiant(map, targetName);
            profiles.Add(new MapEfficiency(estimate.Map, estimate.TargetEnemiesPerPattern,
                estimate.TotalEnemiesPerPattern, estimate.EligiblePatterns, giantDrop));
        }
        return profiles;
    }

    internal static MapEfficiency BestForMaterial(IReadOnlyList<MapEfficiency> profiles, double materialsNeeded)
    {
        if (profiles == null || profiles.Count == 0) return null;
        MapEfficiency best = null;
        var bestSeconds = double.MaxValue;
        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            if (profile?.Map == null || profile.TargetEnemiesPerPattern <= 0) continue;
            var expectedEligibleKills = ExpectedMaterialKills(materialsNeeded, profile.TargetsGiant);
            var estimate = new MapEfficiency(profile.Map, profile.TargetEnemiesPerPattern,
                profile.TotalEnemiesPerPattern, profile.EligiblePatterns, profile.TargetsGiant,
                expectedEligibleKills);
            var seconds = EstimatedWorkSeconds(estimate, Math.Max(1, expectedEligibleKills));
            if (best == null || seconds < bestSeconds)
            {
                best = estimate;
                bestSeconds = seconds;
            }
        }
        return best;
    }

    internal static MapEfficiency BestForPassiveKillAndMaterials(Quest quest)
    {
        var controller = MapController.instance;
        if (quest == null || controller?.maps == null) return null;
        MapEfficiency best = null;
        var bestMaterialUtility = double.NegativeInfinity;
        var bestKillDensity = double.NegativeInfinity;
        var patternsPerMinute = Math.Max(1, Plugin.Config.ExpectedEnemyPatternsPerMinute.Value);
        var horizon = Math.Max(60, Plugin.Config.JointMapPlanningHorizonSeconds.Value);

        for (var i = 0; i < controller.maps.Length; i++)
        {
            var map = controller.maps[i];
            if (!QuestDirector.IsStandardAvailableMap(map)) continue;
            var killEstimate = EvaluateForQuest(map, quest);
            var killDensity = EffectiveDensity(killEstimate);
            if (killDensity <= 0) continue;

            var perPatternMaterialUtility = 0.0;
            foreach (var material in CollectibleDrops(map))
            {
                double capacity;
                try { capacity = material.GetMaxAmount(); }
                catch { capacity = material.maxAmount; }
                if (!double.IsFinite(capacity) || capacity <= 0) continue;
                var missing = Math.Max(0, capacity - material.amount);
                if (missing <= 0.0001) continue;
                var estimate = EvaluateForMaterial(map, material, missing);
                var density = EffectiveDensity(estimate);
                if (density <= 0) continue;
                var deficitRatio = Math.Clamp(missing / capacity, 0, 1);
                var expectedYield = MaterialProgressPerEligibleKill(estimate.TargetsGiant);
                var equivalentPatternDensity = estimate.TargetsGiant
                    ? EstimatedTargetKillsPerMinute(density, true, includeTemporaryShards: false) /
                      patternsPerMinute
                    : density;
                perPatternMaterialUtility += equivalentPatternDensity * expectedYield *
                                             (0.25 + 0.75 * deficitRatio);
            }

        // Maximize expected material gain over one planning horizon first; Portal wait directly reduces production time.
        // When every material is full, quest-target density is the stable fallback.
            var productiveSeconds = Math.Max(0, horizon - EstimatedTravelSeconds(map));
            var materialUtility = perPatternMaterialUtility * patternsPerMinute * productiveSeconds / 60.0;
            if (best == null || materialUtility > bestMaterialUtility + 0.0001 ||
                Math.Abs(materialUtility - bestMaterialUtility) <= 0.0001 && killDensity > bestKillDensity + 0.0001 ||
                Math.Abs(materialUtility - bestMaterialUtility) <= 0.0001 &&
                Math.Abs(killDensity - bestKillDensity) <= 0.0001 && SameMap(map, controller.selectedMap))
            {
                best = killEstimate;
                bestMaterialUtility = materialUtility;
                bestKillDensity = killDensity;
            }
        }
        return best;
    }

    internal static MapEfficiency EvaluateForMaterial(BaseMap map, Drop material, double materialsNeeded)
    {
        if (map == null || material == null) return new MapEfficiency(map, 0, 0, 0);
        var targetName = NormalizedName(material);
        if (string.IsNullOrEmpty(targetName)) return new MapEfficiency(map, 0, 0, 0);
        var estimate = Evaluate(map, enemy =>
        {
            var current = CurrentStage(enemy);
            return CanAutomateEnemy(current) && EnemyDrops(current, targetName);
        }, allowMembershipFallback: true);
        var giantDrop = MapTargetDropsFromGiant(map, targetName);
        return new MapEfficiency(estimate.Map, estimate.TargetEnemiesPerPattern, estimate.TotalEnemiesPerPattern,
            estimate.EligiblePatterns, giantDrop, ExpectedMaterialKills(materialsNeeded, giantDrop));
    }

    internal static IEnumerable<Drop> CollectibleDrops(BaseMap map)
    {
        var seen = new HashSet<int>();
        foreach (var enemy in EnumerateCurrentMapEnemies(map))
        {
            var drop = enemy?.drop;
            if (!CanCollectMaterial(drop)) continue;
            int id;
            try { id = drop.GetInstanceID(); }
            catch { continue; }
            if (seen.Add(id)) yield return drop;
        }
    }

    internal static MapEfficiency BestForAnyMaterial(double materialsNeeded)
    {
        var controller = MapController.instance;
        if (controller?.maps == null) return null;
        MapEfficiency best = null;
        var bestSeconds = double.MaxValue;
        for (var i = 0; i < controller.maps.Length; i++)
        {
            var map = controller.maps[i];
            if (!QuestDirector.IsStandardAvailableMap(map)) continue;
            var estimate = EvaluateForMaterialQuest(map);
            if (estimate.TargetEnemiesPerPattern <= 0) continue;
            var expectedEligibleKills = ExpectedMaterialKills(materialsNeeded, estimate.TargetsGiant);
            estimate = new MapEfficiency(estimate.Map, estimate.TargetEnemiesPerPattern, estimate.TotalEnemiesPerPattern,
                estimate.EligiblePatterns, estimate.TargetsGiant, expectedEligibleKills);
            var seconds = EstimatedWorkSeconds(estimate, Math.Max(1, expectedEligibleKills));
            if (best == null || seconds < bestSeconds)
            {
                best = estimate;
                bestSeconds = seconds;
            }
        }
        return best;
    }

    internal static MapEfficiency EvaluateForQuest(BaseMap map, Quest quest) =>
        Evaluate(map, enemy => QuestEnemyMatches(enemy, quest), allowMembershipFallback: true);

    internal static MapEfficiency EvaluateForAllEnemies(BaseMap map) => Evaluate(map, enemy =>
    {
        var current = CurrentStage(enemy);
        return CanAutomateEnemy(current);
    }, allowMembershipFallback: true);

    internal static MapEfficiency EvaluateForMaterialQuest(BaseMap map)
    {
        var estimate = Evaluate(map, enemy =>
        {
            var current = CurrentStage(enemy);
            return current != null && CanCollectMaterial(current.drop) &&
                CanAutomateEnemy(current);
        }, allowMembershipFallback: true);
        var giantOnly = MapHasOnlyGiantMaterialTargets(map);
        return new MapEfficiency(estimate.Map, estimate.TargetEnemiesPerPattern, estimate.TotalEnemiesPerPattern,
            estimate.EligiblePatterns, giantOnly);
    }

    internal static MapEfficiency Evaluate(BaseMap map, Func<Enemy, bool> targetPredicate,
        bool allowMembershipFallback = false)
    {
        if (map == null || targetPredicate == null) return new MapEfficiency(map, 0, 0, 0);
        var patterns = MapController.instance?.enemyPatterns;
        var unlockedLevel = Math.Max(0, PlayerInventory.instance?.enemyPatternLevels ?? 0);
        double totalTargets = 0;
        double totalEnemies = 0;
        var eligible = 0;

        try
        {
            if (patterns != null)
            {
                for (var i = 0; i < patterns.Length; i++)
                {
                    var pattern = patterns[i];
                    if (pattern == null || pattern.level > unlockedLevel || !PatternSupportsMap(pattern, map)) continue;
                    var enemies = pattern.enemies;
                    if (enemies == null || enemies.Length == 0) continue;
                    var patternTargets = 0;
                    var patternEnemies = 0;
                    for (var j = 0; j < enemies.Length; j++)
                    {
                        var enemyObject = enemies[j];
                        var enemy = CurrentStage(enemyObject?.enemyStats?.enemy);
                        if (!IsEnemyUnlocked(enemy)) continue;
                        patternEnemies++;
                        if (targetPredicate(enemy)) patternTargets++;
                    }
                    eligible++;
                    totalTargets += patternTargets;
                    totalEnemies += patternEnemies;
                }
            }
        }
        catch (Exception ex)
        {
            if (Plugin.Config.DebugLogging.Value)
                SmartLog.Debug(SmartLogCategory.Planning,
                    $"Unable to read live enemy-pattern density; using the map enemy table: {ex.GetType().Name}: {ex.Message}");
        }

        if (eligible > 0 && totalTargets > 0)
            return new MapEfficiency(map, totalTargets / eligible, totalEnemies / eligible, eligible);

        // Newly added/special maps may not expose pattern metadata. A membership fallback is safer than
        // declaring the target impossible, but always ranks below a measured positive-density map.
        var fallbackTargets = 0;
        var fallbackTotal = 0;
        try
        {
            var enemies = map.currentStageEnemies != null && map.currentStageEnemies.Count > 0
                ? map.currentStageEnemies.ToArray()
                : map.enemies;
            if (enemies != null)
            {
                for (var i = 0; i < enemies.Length; i++)
                {
                    var enemy = CurrentStage(enemies[i]);
                    if (!IsEnemyUnlocked(enemy)) continue;
                    fallbackTotal++;
        // Some versions leave enemyStats unbound in pattern entries. Unlocked current-evolution map members are still
        // reachable; callers may explicitly use conservative density to avoid treating the whole map as zero value.
                    if (targetPredicate(enemy) && (allowMembershipFallback || eligible == 0 || enemy.isGiant))
                        fallbackTargets++;
                }
            }
        }
        catch { /* Uninitialized story maps are already filtered; keep a zero estimate on races. */ }
        return new MapEfficiency(map, fallbackTargets > 0 ? fallbackTargets * 0.01 : 0, fallbackTotal, 0);
    }

    private static SoulMapEfficiency EvaluateSouls(BaseMap map)
    {
        if (map == null) return new SoulMapEfficiency(map, 0, 0);
        var patterns = MapController.instance?.enemyPatterns;
        var unlockedLevel = Math.Max(0, PlayerInventory.instance?.enemyPatternLevels ?? 0);
        var totalSouls = 0.0;
        var eligible = 0;

        try
        {
            if (patterns != null)
            {
                for (var i = 0; i < patterns.Length; i++)
                {
                    var pattern = patterns[i];
                    if (pattern == null || pattern.level > unlockedLevel || !PatternSupportsMap(pattern, map)) continue;
                    var enemies = pattern.enemies;
                    if (enemies == null || enemies.Length == 0) continue;
                    var patternSouls = 0.0;
                    for (var j = 0; j < enemies.Length; j++)
                    {
                        var stats = enemies[j]?.enemyStats;
                        var enemy = CurrentStage(stats?.enemy);
                        if (!IsEnemyUnlocked(enemy) || !CanAutomateEnemy(enemy)) continue;
                        patternSouls = SaturatingAdd(patternSouls, RuntimeSoulsReward(stats, enemy));
                    }
                    totalSouls = SaturatingAdd(totalSouls, patternSouls);
                    eligible++;
                }
            }
        }
        catch (Exception ex)
        {
            if (Plugin.Config.DebugLogging.Value)
                SmartLog.Debug(SmartLogCategory.Planning,
                    $"Unable to read live map Souls expectations; using the map enemy table: {ex.GetType().Name}: {ex.Message}");
        }

        if (eligible > 0 && totalSouls > 0)
            return new SoulMapEfficiency(map, ApplyMapSoulMultiplier(map, totalSouls / eligible), eligible);

        // A special map without pattern metadata can still compare base Souls from current-evolution enemies, but is
        // conservatively discounted so an incomplete member table cannot outrank measured patterns.
        var fallbackSouls = 0.0;
        foreach (var enemy in EnumerateCurrentMapEnemies(map))
        {
            if (!CanAutomateEnemy(enemy)) continue;
            fallbackSouls = SaturatingAdd(fallbackSouls, BaseSoulsReward(enemy));
        }
        var fallbackScore = fallbackSouls > 0 ? fallbackSouls * 0.01 : 0;
        return new SoulMapEfficiency(map, ApplyMapSoulMultiplier(map, fallbackScore), 0);
    }

    private static double ApplyMapSoulMultiplier(BaseMap map, double souls)
    {
        if (map == null || !double.IsFinite(souls) || souls <= 0) return 0;
        try
        {
        // Use the native map-event multiplier shared with the Portal panel so map-specific Souls bonuses are not lost.
        // coins is the second output of that native API and is not used by this score.
            var portals = PopupPortals.instance;
            if (portals == null) return souls;
            var adjustedSouls = souls;
            var ignoredCoins = 1.0;
            portals.ApplyMapEventMultipliers(map, ref adjustedSouls, ref ignoredCoins);
            return double.IsFinite(adjustedSouls) && adjustedSouls > 0 ? adjustedSouls : souls;
        }
        catch
        {
            return souls;
        }
    }

    private static double RuntimeSoulsReward(EnemyStats stats, Enemy enemy)
    {
        try
        {
            var reward = stats?.GetSoulsReward() ?? 0;
            if (double.IsFinite(reward) && reward > 0) return reward;
        }
        catch { /* Fall back to current-evolution base rewards while scene objects initialize. */ }
        return BaseSoulsReward(enemy);
    }

    private static double BaseSoulsReward(Enemy enemy)
    {
        try
        {
            var reward = CurrentStage(enemy)?.soulsReward ?? 0;
            return double.IsFinite(reward) && reward > 0 ? reward : 0;
        }
        catch { return 0; }
    }

    private static double SaturatingAdd(double left, double right)
    {
        if (!double.IsFinite(right) || right <= 0) return left;
        if (!double.IsFinite(left) || left >= double.MaxValue - right) return double.MaxValue;
        return left + right;
    }

    private static bool IsMeaningfullyGreater(double candidate, double current) =>
        candidate > current && (current <= 0 || (candidate - current) / current > 0.000001);

    private static bool NearlyEqual(double left, double right)
    {
        if (left == right) return true;
        var scale = Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
        return Math.Abs(left - right) / scale <= 0.000001;
    }

    internal static bool SameMap(BaseMap left, BaseMap right)
    {
        if (left == null || right == null) return false;
        var leftId = left.id ?? left.name;
        var rightId = right.id ?? right.name;
        return left == right || (!string.IsNullOrEmpty(leftId) && leftId == rightId);
    }

    internal static double EstimatedTravelSeconds(BaseMap target)
    {
        if (target == null || SameMap(target, MapController.instance?.selectedMap)) return 0;
        return PortalTimingEstimator.FirstTransitionSeconds();
    }

    private static double EstimatedWorkSeconds(MapEfficiency estimate, double requiredTargetEnemies)
    {
        var density = estimate.EligiblePatterns > 0
            ? estimate.TargetEnemiesPerPattern
            : estimate.TargetEnemiesPerPattern * 100;
        var killsPerMinute = EstimatedTargetKillsPerMinute(density, estimate.TargetsGiant,
            includeTemporaryShards: false);
        return EstimatedTravelSeconds(estimate.Map) + requiredTargetEnemies / killsPerMinute * 60;
    }

    internal static double EstimatedTargetKillsPerMinute(double density, bool targetsGiant,
        bool includeTemporaryShards = true)
    {
        if (!targetsGiant)
            return Math.Max(0.25, density) * Math.Max(1, Plugin.Config.ExpectedEnemyPatternsPerMinute.Value);

        // Giants use a separate distance-based spawner. Pattern membership only tells us which giant can
        // appear in a map; treating that entry as one giant per normal enemy pattern caused 10-40x optimism.
        var targetShare = Math.Clamp(density, 0.01, 1.0);
        var spawnFactor = CurrentGiantSpawnFactor(includeTemporaryShards);
        var interval = GiantKillTimingBridge.BaseIntervalSeconds / Math.Max(1, spawnFactor);
        return Math.Max(0.01, 60.0 / Math.Max(5, interval) * targetShare);
    }

    internal static double CurrentGiantSpawnFactor(bool includeTemporaryShards)
    {
        var modifier = 0.0;
        try { modifier = Math.Max(0, PlayerInventory.instance?.giantsChanceModifier ?? 0); }
        catch { /* Scene initialization uses the base rate. */ }
        if (!includeTemporaryShards && StrategyBehaviour.SmartItemActive(SmartItem.ShardsNecklace))
            modifier = Math.Max(0, modifier - 300);
        return Math.Max(1, 1 + modifier / 100.0);
    }

    internal static bool HasCollectibleMaterialSpace()
    {
        var controller = MapController.instance;
        if (controller?.maps == null) return false;
        for (var i = 0; i < controller.maps.Length; i++)
        {
            var map = controller.maps[i];
            if (!QuestDirector.IsStandardAvailableMap(map)) continue;
            foreach (var enemy in EnumerateCurrentMapEnemies(map))
                if (CanAutomateEnemy(enemy) && CanCollectMaterial(enemy?.drop)) return true;
        }
        return false;
    }

    private static bool PatternSupportsMap(EnemyPattern pattern, BaseMap map)
    {
        var maps = pattern.maps;
        if (maps == null || maps.Length == 0) return true;
        for (var i = 0; i < maps.Length; i++)
            if (SameMap(maps[i], map)) return true;
        return false;
    }

    internal static bool QuestEnemyMatches(Enemy enemy, Quest quest)
    {
        enemy = CurrentStage(enemy);
        if (!IsEnemyUnlocked(enemy) || quest == null) return false;
        var rageCanHandleTarget = quest.questType == QuestType.KillEnemiesWithRageMode ||
            QuestDirector.TargetCannotBeKilledWithArrows(quest);
        if (quest.questType == QuestType.WindDashKills && !StrategyBehaviour.CanAutomateWithWindDash(enemy))
            return false;
        if (Plugin.Config.AssumeBowAutomation.Value && !rageCanHandleTarget &&
            !StrategyBehaviour.CanAutomateWithWindDash(enemy) &&
            quest.questType != QuestType.WindDashKills && enemy.cantBeKilledWithArrows) return false;
        if (quest.questType == QuestType.KillGiants) return enemy.isGiant;
        if (quest.questType == QuestType.KillFlyers) return enemy.isFlying;
        if (quest.enemyToKill != null || quest.enemyType != null)
            return CurrentOrBackwardStageMatches(enemy, quest.enemyToKill, quest.enemyType);
        return true;
    }

    private static bool ExactEnemyMatch(Enemy enemy, Enemy target, EnemyType targetType)
    {
        if (enemy == null) return false;
        if (target != null)
        {
            var targetName = NormalizedName(target);
            if (!string.IsNullOrEmpty(targetName) && NormalizedName(enemy) == targetName) return true;
        }
        return targetType != null && enemy.type == targetType;
    }

    private static bool EnemyDrops(Enemy enemy, string targetName) =>
        NormalizedName(CurrentStage(enemy)?.drop) == targetName;

    private static bool CurrentOrBackwardStageMatches(Enemy enemy, Enemy target, EnemyType targetType)
    {
        var seen = new HashSet<int>();
        for (var depth = 0; enemy != null && depth < 12; depth++)
        {
            var id = enemy.GetInstanceID();
            if (!seen.Add(id)) break;
            if (ExactEnemyMatch(enemy, target, targetType)) return true;
            enemy = enemy.evolutionBackward;
        }
        return false;
    }

    internal static Enemy CurrentStage(Enemy enemy)
    {
        if (enemy == null) return null;
        try { return enemy.GetCurrentStage() ?? enemy; }
        catch { return enemy; }
    }

    internal static bool IsEnemyUnlocked(Enemy enemy)
    {
        enemy = CurrentStage(enemy);
        if (enemy == null) return false;
        try { return enemy.upgradeToUnlockIt == null || enemy.upgradeToUnlockIt.bought; }
        catch { return false; }
    }

    internal static bool CanAutomateEnemy(Enemy enemy)
    {
        if (enemy == null) return false;
        if (!Plugin.Config.AssumeBowAutomation.Value || !enemy.cantBeKilledWithArrows) return true;
        if (StrategyBehaviour.CanAutomateWithWindDash(enemy)) return true;
        // Regular arrows cannot kill Giants, but native BattleMode rapid melee can automate them reliably.
        return enemy.isGiant && Plugin.Config.GiantRapidAttackEnabled.Value;
    }

    internal static double ExpectedMaterialKills(double materialsNeeded, bool giantDrop)
    {
        var inventory = PlayerInventory.instance;
        if (inventory == null) return materialsNeeded / 0.10;
        try
        {
            return StrategyMath.ExpectedEligibleKills(materialsNeeded, inventory.dropChance,
                inventory.giantsDropChance, inventory.doubleMaterialsChance, giantDrop);
        }
        catch { return materialsNeeded / 0.10; }
    }

    internal static double MaterialProgressPerEligibleKill(bool giantDrop)
    {
        var kills = ExpectedMaterialKills(1, giantDrop);
        return double.IsFinite(kills) && kills > 0 ? 1 / kills : 0;
    }

    internal static double EffectiveDensity(MapEfficiency estimate) => estimate == null ? 0 :
        estimate.EligiblePatterns > 0 ? estimate.TargetEnemiesPerPattern : estimate.TargetEnemiesPerPattern * 100;

    private static bool MapTargetDropsFromGiant(BaseMap map, string targetName) =>
        EnumerateCurrentMapEnemies(map).Any(enemy => enemy.isGiant && NormalizedName(enemy.drop) == targetName);

    private static bool MapHasOnlyGiantMaterialTargets(BaseMap map)
    {
        var targets = EnumerateCurrentMapEnemies(map).Where(enemy => CanCollectMaterial(enemy.drop)).ToArray();
        return targets.Length > 0 && targets.All(enemy => enemy.isGiant);
    }

    private static bool CanCollectMaterial(Drop drop)
    {
        if (drop == null || drop.locked) return false;
        try
        {
            var capacity = drop.GetMaxAmount();
            return capacity <= 0 || drop.amount + 0.0001 < capacity;
        }
        catch { return drop.maxAmount <= 0 || drop.amount + 0.0001 < drop.maxAmount; }
    }

    private static IEnumerable<Enemy> EnumerateCurrentMapEnemies(BaseMap map)
    {
        if (map == null) yield break;
        Enemy[] enemies;
        try
        {
            enemies = map.currentStageEnemies != null && map.currentStageEnemies.Count > 0
                ? map.currentStageEnemies.ToArray()
                : map.enemies;
        }
        catch { yield break; }
        if (enemies == null) yield break;
        var seen = new HashSet<int>();
        for (var i = 0; i < enemies.Length; i++)
        {
            var current = CurrentStage(enemies[i]);
            if (!IsEnemyUnlocked(current) || !seen.Add(current.GetInstanceID())) continue;
            yield return current;
        }
    }

    private static string NormalizedName(UnityEngine.Object value)
    {
        if (value == null) return string.Empty;
        var name = CraftableCatalog.Normalize(value.name);
        if (value is Drop drop && string.IsNullOrEmpty(name)) name = CraftableCatalog.Normalize(drop.localizedName);
        return name;
    }
}

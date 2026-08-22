namespace SmartStrategy;

internal readonly record struct KillQuestTimedBonus(double Bonus, double DurationSeconds);

internal readonly record struct KillQuestEstimate(double Seconds, double KillActions);

internal static class KillQuestEstimator
{
    internal static KillQuestEstimate Estimate(double remainingProgress, double killsPerMinute,
        double baseCredit, IReadOnlyList<KillQuestTimedBonus> timedBonuses = null)
    {
        var progressLeft = Math.Max(0, remainingProgress);
        if (progressLeft <= 0) return new KillQuestEstimate(0, 0);

        var killsPerSecond = Math.Max(0.0001, killsPerMinute / 60.0);
        var elapsed = 0.0;
        var killActions = 0.0;
        var expirations = timedBonuses == null
            ? Array.Empty<double>()
            : timedBonuses.Where(x => x.Bonus > 0 && x.DurationSeconds > 0.0001)
                .Select(x => x.DurationSeconds).Distinct().OrderBy(x => x).ToArray();

        foreach (var expiration in expirations)
        {
            if (expiration <= elapsed + 0.0001) continue;
            var bonus = timedBonuses.Where(x => x.Bonus > 0 && x.DurationSeconds > elapsed + 0.0001)
                .Sum(x => x.Bonus);
            var progressPerSecond = killsPerSecond * Math.Max(1, baseCredit + bonus);
            var interval = expiration - elapsed;
            var possibleProgress = progressPerSecond * interval;
            if (possibleProgress >= progressLeft)
            {
                var neededSeconds = progressLeft / progressPerSecond;
                return new KillQuestEstimate(elapsed + neededSeconds,
                    killActions + killsPerSecond * neededSeconds);
            }

            progressLeft -= possibleProgress;
            killActions += killsPerSecond * interval;
            elapsed = expiration;
        }

        var baseProgressPerSecond = killsPerSecond * Math.Max(1, baseCredit);
        var tailSeconds = progressLeft / baseProgressPerSecond;
        return new KillQuestEstimate(elapsed + tailSeconds, killActions + killsPerSecond * tailSeconds);
    }
}

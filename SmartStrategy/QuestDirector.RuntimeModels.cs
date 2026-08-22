using Il2Cpp;

namespace SmartStrategy;

internal sealed partial class QuestDirector
{
    private sealed class QuestProgressObservation
    {
        internal QuestType Type { get; }
        internal int InitialProgress { get; }
        internal float FirstSeenAt { get; }
        internal int Progress { get; set; }
        internal float ProgressAt { get; set; }
        internal double ActiveElapsedSeconds { get; set; }

        internal QuestProgressObservation(QuestType type, int progress, float progressAt)
        {
            Type = type;
            InitialProgress = progress;
            FirstSeenAt = progressAt;
            Progress = progress;
            ProgressAt = progressAt;
            ActiveElapsedSeconds = 0;
        }
    }

    private sealed class QuestRuntimeCalibration
    {
        internal double SecondsPerProgress { get; private set; }
        internal int Samples { get; private set; }

        internal QuestRuntimeCalibration() { }

        internal QuestRuntimeCalibration(double secondsPerProgress, int samples)
        {
            SecondsPerProgress = secondsPerProgress;
            Samples = samples;
        }

        internal void Observe(double secondsPerProgress)
        {
            if (!double.IsFinite(secondsPerProgress) || secondsPerProgress <= 0) return;
            SecondsPerProgress = Samples == 0
                ? secondsPerProgress
                : SecondsPerProgress * 0.75 + secondsPerProgress * 0.25;
            Samples++;
        }
    }

    private sealed class QuestEntry
    {
        internal Quest Quest { get; }
        internal QuestSource Source { get; }

        internal QuestEntry(Quest quest, QuestSource source)
        {
            Quest = quest;
            Source = source;
        }
    }
}

namespace Backtest_for_new_orb;

/// <summary>
/// Chronological expanding-window walk-forward split. All indices are [start, end).
/// No test session is ever included in the training period for that fold.
/// </summary>
public sealed record WalkForwardFold(
    int Number, int TrainingStart, int TrainingEnd,
    int TestStart, int TestEnd)
{
    public int TrainingSessions => TrainingEnd - TrainingStart;
    public int TestSessions => TestEnd - TestStart;
    public int MinimumTrainingTrades => Math.Max(4, (TrainingSessions + 3) / 4);

    public static WalkForwardFold[] Create(int sessionCount)
    {
        if (sessionCount < 20)
            throw new ArgumentException(
                "Walk-forward testing requires at least 20 days with complete opening ranges.",
                nameof(sessionCount));

        // Five consecutive chronological blocks. The first 40% is used
        // exclusively for training; each successive ~20% is tested once.
        // The last fold takes any remainder, with no overlapping test dates.
        int block = sessionCount / 5;
        return [
            new(1, 0, block * 2, block * 2, block * 3),
            new(2, 0, block * 3, block * 3, block * 4),
            new(3, 0, block * 4, block * 4, sessionCount)
        ];
    }
}

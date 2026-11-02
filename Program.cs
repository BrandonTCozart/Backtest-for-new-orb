using Backtest_for_new_orb;

// Pressing Play updates available historical ES candles first, then runs
// the existing full-dataset optimizer and expanding-window walk-forward test.
try
{
    var directory = Path.Combine(BacktestRunner.FindRoot(), "data");
    try
    {
        await TopstepHistoryDownloader.RunAsync(directory);
    }
    catch (Exception error)
    {
        Console.Error.WriteLine("TopstepX historical download failed: " + error.Message);
        Console.Error.WriteLine("Continuing with existing local ES candles.");
    }

    // On a clean data folder, a successful download must create the dataset.
    // Keep supporting all original CSV inputs when they already exist.
    if (!new[] {
            TopstepHistoryDownloader.OutputName,
            "es_1m_3months.csv",
            "es_1m_3months_INCOMPLETE.csv"
        }.Any(name => File.Exists(Path.Combine(directory, name))))
    {
        Console.Error.WriteLine("No ES historical CSV is available. The TopstepX download did not create one.");
        return 1;
    }

    return WalkForwardRunner.Run();
}
catch (Exception error)
{
    Console.Error.WriteLine("Optimization failed: " + error);
    return 1;
}

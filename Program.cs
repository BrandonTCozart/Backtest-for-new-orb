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

    return WalkForwardRunner.Run();
}
catch (Exception error)
{
    Console.Error.WriteLine("Optimization failed: " + error);
    return 1;
}

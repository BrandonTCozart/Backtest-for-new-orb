using Backtest_for_new_orb;

// Visual Studio green Start runs the offline backtest, not the Topstep downloader.
try
{
    return BacktestRunner.Run();
}
catch (Exception error)
{
    Console.Error.WriteLine("Backtest failed: " + error.Message);
    return 1;
}

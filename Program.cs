using Backtest_for_new_orb;

// Pressing Play runs chronological, expanding-window walk-forward testing.
// It never calls Topstep, downloads candles, or places live orders.
try
{
    return WalkForwardRunner.Run();
}
catch (Exception error)
{
    Console.Error.WriteLine("Walk-forward optimization failed: " + error);
    return 1;
}

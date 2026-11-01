using Backtest_for_new_orb;

// Visual Studio Start now explores the finite grid of ORB configurations locally.
// The downloaded ES CSV stays on disk. The live Orb worker is never invoked.
try
{
    return OptimizationRunner.Run();
}
catch (Exception ex)
{
    Console.Error.WriteLine("Optimization failed: " + ex);
    return 1;
}

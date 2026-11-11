using Backtest_for_new_orb;
using Microsoft.Extensions.Configuration;

// Press Play to load ES one-minute SQL history and run multi-strategy research.
// --self-test verifies data/session and simulation invariants without SQL.
try
{
    if (args.Any(x => string.Equals(x, "--self-test", StringComparison.OrdinalIgnoreCase)))
        return MultiStrategySelfTests.Run();

    var root = BacktestRunner.FindRoot();
    var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? "Production";

    var configuration = new ConfigurationBuilder()
        .SetBasePath(root)
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{environment}.json", optional: true)
        .AddJsonFile("appsettings.Local.json", optional: true)
        .AddUserSecrets("dotnet-Backtest_for_new_orb-91f535b9-437c-4a70-ae84-ee85729d8eb9")
        .AddEnvironmentVariables()
        .Build();

    var symbol = configuration["MarketData:Symbol"] ?? "";
    var timeZone = configuration["MarketData:TimestampTimeZone"] ?? "America/New_York";

    var profileText = configuration["Backtest:SearchProfile"] ?? "Deep";
    if (!Enum.TryParse<MultiSearchProfile>(profileText, true, out var searchProfile))
        throw new ArgumentException(
            "Backtest:SearchProfile must be Deep or Smoke (Deep is the full search).");
    var mode = (configuration["Backtest:Mode"] ?? "Multi").Trim().ToLowerInvariant();
    if (mode is "researchthenhistorical" or "combined" or "pipeline")
        return await HistoricalReplayRunner.RunSequentialAsync(
            configuration.GetConnectionString("MarketData"), symbol, timeZone,
            searchProfile, root);
    if (mode is "historicalreplay" or "historical")
        return await HistoricalReplayRunner.RunAsync(
            configuration.GetConnectionString("MarketData"), symbol, timeZone, searchProfile);
    Console.WriteLine($"Reading dbo.MES_DATA from SQL Server; symbol={(string.IsNullOrWhiteSpace(symbol) ? "(auto-detect active contract)" : symbol)}; stored-timezone={timeZone}");
    Console.WriteLine("dbo.MES_DATA contains ES futures candles; ES P&L uses $50 per point and optional Stage-6 MES remains an ES-price proxy.");

    var marketData = await SqlMarketDataLoader.LoadAsync(
        configuration.GetConnectionString("MarketData"), symbol, timeZone);
    var bars = marketData.ActiveBars;
    Console.WriteLine($"SQL active ES candles loaded: {bars.Count:N0}");
    Console.WriteLine($"SQL candle range (UTC): {bars[0].T:O} to {bars[^1].T:O}");


    Console.WriteLine("Backtest mode: " + mode +
        " (Multi=walk-forward grid; ResearchThenHistorical=both SQL periods; HistoricalReplay=legacy pinned replay; Orb=ORB; Both=multi plus ORB).");
    switch (mode)
    {
        case "multi":
            return MultiStrategyRunner.Run(bars, marketData.AllContractBars, root, searchProfile);
        case "orb":
            return WalkForwardRunner.Run(bars, marketData.AllContractBars);
        case "both":
        {
            // Console-only multi-strategy results precede the original ORB optimizer.
            var multiExit = MultiStrategyRunner.Run(
                bars, marketData.AllContractBars, root, searchProfile);
            if (multiExit != 0) return multiExit;
            return WalkForwardRunner.Run(bars, marketData.AllContractBars);
        }
        default:
            throw new ArgumentException(
                "Backtest:Mode must be ResearchThenHistorical, Multi, HistoricalReplay, Orb, or Both (appsettings.json).");
    }
}
catch (Exception error)
{
    Console.Error.WriteLine("Optimization failed: " + error);
    return 1;
}

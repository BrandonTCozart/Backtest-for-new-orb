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
    var scopeText = configuration["Backtest:StrategyScope"] ?? "Focused";
    if (!Enum.TryParse<MultiStrategyScope>(scopeText, true, out var strategyScope) ||
        !Enum.IsDefined(strategyScope))
        throw new ArgumentException(
            "Backtest:StrategyScope must be Focused (five families) or Full (all 19).");
    var configModeText = configuration["Backtest:ConfigurationMode"] ?? "AdaptiveCandles";
    if (!Enum.TryParse<AdaptiveConfigurationMode>(
            configModeText, true, out var configurationMode) ||
        !Enum.IsDefined(configurationMode))
        throw new ArgumentException(
            "Backtest:ConfigurationMode must be Baseline, Adaptive or AdaptiveCandles.");
    var orbPatternGroupsText = configuration["Backtest:OrbPatternGroups"] ?? "4";
    if (!int.TryParse(orbPatternGroupsText, out var orbPatternGroups) ||
        orbPatternGroups is < 2 or > 8)
        throw new ArgumentException(
            "Backtest:OrbPatternGroups must be an integer between 2 and 8.");
    var workersText = configuration["Backtest:ParallelWorkers"] ?? "0";
    if (!int.TryParse(workersText, out var parallelWorkers) ||
        parallelWorkers < 0 || parallelWorkers > 32)
        throw new ArgumentException(
            "Backtest:ParallelWorkers must be 0 (automatic) or 1 through 32.");
    var shapeGroupsText = configuration["Backtest:ShapePatternGroups"] ?? "5";
    if (!int.TryParse(shapeGroupsText, out var shapeGroups) ||
        shapeGroups is < 2 or > 8)
        throw new ArgumentException(
            "Backtest:ShapePatternGroups must be between 2 and 8.");
    var mode = (configuration["Backtest:Mode"] ?? "HistoricalThenRecent").Trim().ToLowerInvariant();
    if (mode is "breakoutqualityresearch" or "breakoutquality")
        return await BreakoutQualityResearch.RunAsync(
            configuration.GetConnectionString("MarketData"), symbol, timeZone);
    if (mode is "openingdirectionresearch" or "shapepremarketresearch")
        return await OpeningDirectionResearch.RunAsync(
            configuration.GetConnectionString("MarketData"), symbol,
            timeZone, shapeGroups);
    if (mode is "orbgroup3replay" or "orbgroup3")
        return await OrbGroup3ReplayRunner.RunAsync(
            configuration.GetConnectionString("MarketData"), symbol, timeZone);
    if (mode is "orbpatternresearch" or "orbpatterns" or "orbclustering")
        return await OrbOpeningPatternExperiment.RunAsync(
            configuration.GetConnectionString("MarketData"), symbol, timeZone,
            searchProfile, orbPatternGroups);
    if (mode is "historicalthenrecent" or "reverse" or "historicaltrainthenrecent")
        return await HistoricalReplayRunner.RunHistoricalThenRecentAsync(
            configuration.GetConnectionString("MarketData"), symbol, timeZone,
            searchProfile, root, parallelWorkers, strategyScope, configurationMode);
    if (mode is "researchthenhistorical" or "combined" or "pipeline")
        return await HistoricalReplayRunner.RunSequentialAsync(
            configuration.GetConnectionString("MarketData"), symbol, timeZone,
            searchProfile, root, parallelWorkers, strategyScope, configurationMode);
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
        " (HistoricalThenRecent=older training + newer forward test; ResearchThenHistorical=reverse; Multi=recent walk-forward; HistoricalReplay=legacy pinned replay; Orb=ORB; Both=multi plus ORB).");
    switch (mode)
    {
        case "multi":
            return MultiStrategyRunner.Run(bars, marketData.AllContractBars, root, searchProfile,
                parallelWorkers, strategyScope, configurationMode);
        case "orb":
            return WalkForwardRunner.Run(bars, marketData.AllContractBars);
        case "both":
        {
            // Console-only multi-strategy results precede the original ORB optimizer.
            var multiExit = MultiStrategyRunner.Run(
                bars, marketData.AllContractBars, root, searchProfile,
                parallelWorkers, strategyScope, configurationMode);
            if (multiExit != 0) return multiExit;
            return WalkForwardRunner.Run(bars, marketData.AllContractBars);
        }
        default:
            throw new ArgumentException(
                "Backtest:Mode must be HistoricalThenRecent, ResearchThenHistorical, Multi, HistoricalReplay, Orb, OrbPatternResearch, OrbGroup3Replay, OpeningDirectionResearch, BreakoutQualityResearch, or Both (appsettings.json).");
    }
}
catch (Exception error)
{
    Console.Error.WriteLine("Optimization failed: " + error);
    return 1;
}

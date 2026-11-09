using Backtest_for_new_orb;
using Microsoft.Extensions.Configuration;

// Press Play to load the same SQL Server candle table as orb-analysis, then
// run the unchanged full-dataset optimizer and expanding-window walk-forward tests.
try
{
    var root = BacktestRunner.FindRoot();
    var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? "Production";

    var configuration = new ConfigurationBuilder()
        .SetBasePath(root)
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{environment}.json", optional: true)
        .AddUserSecrets("dotnet-Backtest_for_new_orb-91f535b9-437c-4a70-ae84-ee85729d8eb9")
        .AddEnvironmentVariables()
        .Build();

    var symbol = configuration["MarketData:Symbol"] ?? "MES";
    var timeZone = configuration["MarketData:TimestampTimeZone"] ?? "America/New_York";
    Console.WriteLine($"Reading dbo.MES_DATA from SQL Server; symbol='{symbol}'; stored-timezone={timeZone}");
    Console.WriteLine("Data are MES candles; optimizer retains its original ES-dollar and MES-proxy trade assumptions.");

    var bars = await SqlMarketDataLoader.LoadAsync(
        configuration.GetConnectionString("MarketData"), symbol, timeZone);
    Console.WriteLine($"SQL candles loaded: {bars.Count:N0}");
    Console.WriteLine($"SQL candle range (UTC): {bars[0].T:O} to {bars[^1].T:O}");

    return WalkForwardRunner.Run(bars);
}
catch (Exception error)
{
    Console.Error.WriteLine("Optimization failed: " + error);
    return 1;
}
